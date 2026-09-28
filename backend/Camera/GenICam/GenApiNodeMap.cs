using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace MTCVision.Backend.Camera.GenICam;

/// <summary>
/// Resolves GenICam feature names to camera registers by walking the node map the camera serves in
/// its own GenICam XML.
///
/// This exists because the Genie Nano reaches almost nothing through a plain "feature name to fixed
/// address" lookup: Width and ExposureTime sit behind indirect addresses (pAddress), Gain and
/// AcquisitionFrameRate behind Converter formulas, and the address of Width itself behind an
/// IntSwissKnife expression. GigeVision.Core resolves none of those and returns 0 instead.
/// </summary>
public sealed class GenApiNodeMap
{
    private readonly GvcpClient _gvcp;
    private readonly Dictionary<string, XElement> _nodes;

    private GenApiNodeMap(GvcpClient gvcp, Dictionary<string, XElement> nodes)
    {
        _gvcp = gvcp;
        _nodes = nodes;
    }

    public string XmlFileName { get; private init; } = string.Empty;

    public IReadOnlyCollection<string> FeatureNames => _nodes.Keys;

    public bool Has(string featureName) => _nodes.ContainsKey(featureName);

    /// <summary>
    /// Downloads the GenICam XML the camera keeps in device memory (usually zipped) and indexes
    /// every named node in it.
    /// </summary>
    public static async Task<GenApiNodeMap> LoadAsync(GvcpClient gvcp)
    {
        var url = await gvcp.ReadXmlUrlAsync();

        // "Local:<file name>;<hex address>;<hex length>"
        var parts = url.Split(';');
        if (parts.Length < 3 || !url.StartsWith("Local:", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Kameranın XML adresi desteklenmiyor: {url}");
        }

        var fileName = parts[0]["Local:".Length..].Trim();
        var address = uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var length = int.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var blob = await gvcp.ReadMemoryAsync(address, length);

        XDocument document;
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = new ZipArchive(new MemoryStream(blob), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Kameranın XML arşivinde .xml dosyası yok.");
            using var stream = entry.Open();
            document = XDocument.Load(stream);
        }
        else
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(blob).TrimEnd('\0'));
        }

        // Nodes are nested inside Group elements, so index every named descendant rather than
        // only the document root's children. EnumEntry names are scoped to their Enumeration and
        // collide with real features — "AcquisitionStart" is both a Command and a counter-event
        // enum entry — so they stay out of the global map and are resolved through their parent.
        var nodes = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in document.Descendants())
        {
            var name = element.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(name) && element.Name.LocalName != "EnumEntry")
            {
                nodes.TryAdd(name, element);
            }
        }

        return new GenApiNodeMap(gvcp, nodes) { XmlFileName = fileName };
    }

    public async Task<double> GetValueAsync(string featureName) => await ReadNodeAsync(Node(featureName));

    public async Task<double?> TryGetValueAsync(string featureName)
    {
        if (!_nodes.TryGetValue(featureName, out var node))
        {
            return null;
        }

        try
        {
            return await ReadNodeAsync(node);
        }
        catch (Exception)
        {
            // A feature can be present in the XML but unreadable in the camera's current mode.
            return null;
        }
    }

    public Task SetValueAsync(string featureName, double value) => WriteNodeAsync(Node(featureName), value);

    /// <summary>
    /// The range the camera will accept for a feature. Min/Max are declared on the raw register-side
    /// node, below any Converter, so they are read there and mapped back up through the conversion.
    /// </summary>
    public async Task<FeatureRange> GetRangeAsync(string featureName) => await ReadRangeAsync(Node(featureName));

    /// <summary>
    /// Writes a value clamped into the feature's own range and returns what was actually written.
    /// A value outside the range is refused by the camera with an opaque GVCP status, so clamping
    /// here is what turns "gain 11.1" into the nearest setting it will take.
    /// </summary>
    public async Task<double> SetValueClampedAsync(string featureName, double value)
    {
        var node = Node(featureName);
        var range = await ReadRangeAsync(node);
        var clamped = range.Clamp(value);
        await WriteNodeAsync(node, clamped, skipIfUnchanged: true);
        return clamped;
    }

    private async Task<FeatureRange> ReadRangeAsync(XElement node)
    {
        switch (node.Name.LocalName)
        {
            case "Integer":
            case "Float":
            {
                var min = await LimitAsync(node, "Min", "pMin");
                var max = await LimitAsync(node, "Max", "pMax");
                var inc = await LimitAsync(node, "Inc", "pInc");

                // Anything this node does not declare is inherited from the node it wraps.
                var inner = ChildText(node, "pValue") is null
                    ? FeatureRange.Unbounded
                    : await ReadRangeAsync(Reference(node, "pValue"));

                return new FeatureRange(min ?? inner.Min, max ?? inner.Max, inc ?? inner.Increment);
            }

            case "IntConverter":
            case "Converter":
            {
                var inner = await ReadRangeAsync(Reference(node, "pValue"));
                var variables = await ReadVariablesAsync(node);
                var formula = ChildText(node, "FormulaFrom");
                if (formula is null)
                {
                    return FeatureRange.Unbounded;
                }

                double? Map(double? rawLimit)
                {
                    if (rawLimit is null)
                    {
                        return null;
                    }

                    var bound = new Dictionary<string, double>(variables, StringComparer.Ordinal) { ["TO"] = rawLimit.Value };
                    return GenApiFormula.Evaluate(formula, bound);
                }

                var low = Map(inner.Min);
                var high = Map(inner.Max);

                // A Slope of Decreasing swaps which raw end is the larger converted value.
                if (low is not null && high is not null && low > high)
                {
                    (low, high) = (high, low);
                }

                // An increment does not survive a non-linear conversion (Gain is logarithmic here).
                return new FeatureRange(low, high, null);
            }

            default:
                return FeatureRange.Unbounded;
        }
    }

    private async Task<double?> LimitAsync(XElement node, string literalName, string referenceName)
    {
        var literal = ChildText(node, literalName);
        if (literal is not null)
        {
            return ParseLiteral(literal);
        }

        var referenced = ChildText(node, referenceName);
        if (referenced is null)
        {
            return null;
        }

        try
        {
            return await ReadNodeAsync(Node(referenced));
        }
        catch (Exception)
        {
            // A limit node can be unreadable in the camera's current mode; treat it as unbounded.
            return null;
        }
    }

    /// <summary>
    /// Reads a StringReg: a fixed-length ASCII field in device memory, NUL-padded to its length.
    /// </summary>
    public async Task<string?> TryGetStringAsync(string featureName)
    {
        if (!_nodes.TryGetValue(featureName, out var node) || node.Name.LocalName != "StringReg")
        {
            return null;
        }

        try
        {
            var address = await ResolveAddressAsync(node);
            var length = int.Parse(ChildText(node, "Length") ?? "16", CultureInfo.InvariantCulture);
            var raw = await _gvcp.ReadMemoryAsync(address, length);
            return Encoding.ASCII.GetString(raw).TrimEnd('\0').Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task SetStringAsync(string featureName, string value)
    {
        var node = Node(featureName);
        if (node.Name.LocalName != "StringReg")
        {
            throw new InvalidOperationException($"'{featureName}' bir metin alanı değil.");
        }

        var address = await ResolveAddressAsync(node);
        var length = int.Parse(ChildText(node, "Length") ?? "16", CultureInfo.InvariantCulture);

        // The field is fixed width and must stay NUL-terminated, so the last byte is never written.
        var bytes = new byte[length];
        var text = Encoding.ASCII.GetBytes(value);
        Array.Copy(text, bytes, Math.Min(text.Length, length - 1));

        await _gvcp.WriteMemoryAsync(address, bytes);
    }

    /// <summary>Reads an Enumeration, or null if the camera does not expose it.</summary>
    public async Task<string?> TryGetEnumerationAsync(string featureName)
    {
        if (!_nodes.ContainsKey(featureName))
        {
            return null;
        }

        try
        {
            return await GetEnumerationAsync(featureName);
        }
        catch (Exception)
        {
            // Present in the XML but unreadable in the camera's current mode.
            return null;
        }
    }

    /// <summary>Reads an Enumeration and returns the name of the entry currently selected.</summary>
    public async Task<string?> GetEnumerationAsync(string featureName)
    {
        var node = Node(featureName);
        var value = await ReadNodeAsync(node);
        foreach (var entry in node.Elements().Where(e => e.Name.LocalName == "EnumEntry"))
        {
            var entryValue = await ReadEnumEntryValueAsync(entry);
            if (entryValue is not null && Math.Abs(entryValue.Value - value) < 0.5)
            {
                return entry.Attribute("Name")?.Value;
            }
        }

        return null;
    }

    public async Task SetEnumerationAsync(string featureName, string entryName)
    {
        var node = Node(featureName);
        var entry = node.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "EnumEntry" && e.Attribute("Name")?.Value == entryName)
            ?? throw new KeyNotFoundException($"'{featureName}' özelliğinde '{entryName}' seçeneği yok.");

        var value = await ReadEnumEntryValueAsync(entry)
            ?? throw new InvalidOperationException($"'{entryName}' seçeneğinin değeri okunamadı.");

        await WriteNodeAsync(node, value);
    }

    /// <summary>Fires a Command node (e.g. AcquisitionStart).</summary>
    public async Task ExecuteAsync(string commandName)
    {
        var node = Node(commandName);
        var commandValue = ChildText(node, "CommandValue");
        var value = commandValue is not null
            ? ParseLiteral(commandValue)
            : await ResolveReferenceAsync(node, "pCommandValue") ?? 1;

        try
        {
            await WriteNodeAsync(Reference(node, "pValue"), value);
        }
        catch (GvcpException ex)
        {
            throw new GenApiException($"'{commandName}' komutu kamera tarafından reddedildi: {ex.Message}", ex);
        }
    }

    private XElement Node(string featureName) =>
        _nodes.TryGetValue(featureName, out var node)
            ? node
            : throw new KeyNotFoundException($"Kameranın özellik ağacında '{featureName}' yok.");

    private XElement Reference(XElement node, string childName)
    {
        var referenced = ChildText(node, childName)
            ?? throw new InvalidOperationException($"'{node.Attribute("Name")?.Value}' düğümünde {childName} yok.");
        return Node(referenced);
    }

    private async Task<double?> ResolveReferenceAsync(XElement node, string childName)
    {
        var referenced = ChildText(node, childName);
        return referenced is null ? null : await ReadNodeAsync(Node(referenced));
    }

    private async Task<double?> ReadEnumEntryValueAsync(XElement entry)
    {
        var literal = ChildText(entry, "Value");
        return literal is not null ? ParseLiteral(literal) : await ResolveReferenceAsync(entry, "pValue");
    }

    private async Task<double> ReadNodeAsync(XElement node)
    {
        switch (node.Name.LocalName)
        {
            case "Integer":
            case "Float":
                return await ReadValueNodeAsync(node);

            case "IntReg":
            case "MaskedIntReg":
            case "StructEntry":
            case "FloatReg":
                return await ReadRegisterNodeAsync(node);

            case "IntConverter":
            case "Converter":
            {
                // FormulaFrom turns the linked node's value, bound as TO, into this node's value.
                var variables = await ReadVariablesAsync(node);
                variables["TO"] = await ReadNodeAsync(Reference(node, "pValue"));
                return GenApiFormula.Evaluate(
                    ChildText(node, "FormulaFrom") ?? throw new InvalidOperationException("Converter'da FormulaFrom yok."),
                    variables);
            }

            case "IntSwissKnife":
            case "SwissKnife":
                return GenApiFormula.Evaluate(
                    ChildText(node, "Formula") ?? throw new InvalidOperationException("SwissKnife'ta Formula yok."),
                    await ReadVariablesAsync(node));

            case "Enumeration":
                return await ReadNodeAsync(Reference(node, "pValue"));

            case "Boolean":
            {
                var raw = await ReadNodeAsync(Reference(node, "pValue"));
                var onValue = ChildText(node, "OnValue");
                return onValue is null ? raw : (Math.Abs(raw - ParseLiteral(onValue)) < 0.5 ? 1 : 0);
            }

            default:
                throw new NotSupportedException($"'{node.Name.LocalName}' düğüm tipi okunamıyor.");
        }
    }

    private async Task<double> ReadValueNodeAsync(XElement node)
    {
        // Indexed integers pick their value from a pIndex selector — a per-selector table. Each
        // entry is either a literal (ValueIndexed) or a reference to another node (pValueIndexed),
        // and the camera's XML uses both forms.
        var indexReference = ChildText(node, "pIndex");
        if (indexReference is not null)
        {
            var index = (long)await ReadNodeAsync(Node(indexReference));

            var entry = node.Elements().FirstOrDefault(e =>
                e.Name.LocalName is "ValueIndexed" or "pValueIndexed"
                && long.TryParse(e.Attribute("Index")?.Value, out var i) && i == index)
                ?? node.Elements().FirstOrDefault(e => e.Name.LocalName is "ValueDefault" or "pValueDefault")
                ?? throw new InvalidOperationException(
                    $"'{node.Attribute("Name")?.Value}' için {index} indeksi çözülemedi.");

            return entry.Name.LocalName.StartsWith('p')
                ? await ReadNodeAsync(Node(entry.Value.Trim()))
                : ParseLiteral(entry.Value.Trim());
        }

        var literal = ChildText(node, "Value");
        if (literal is not null)
        {
            return ParseLiteral(literal);
        }

        return await ReadNodeAsync(Reference(node, "pValue"));
    }

    /// <summary>
    /// The names a node's formula may use: its pVariable bindings, plus any Expression children.
    /// Expressions are sub-formulas declared inline and are evaluated in document order, because a
    /// later one may build on an earlier one.
    /// </summary>
    private async Task<Dictionary<string, double>> ReadVariablesAsync(XElement node)
    {
        var variables = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var variable in node.Elements().Where(e => e.Name.LocalName == "pVariable"))
        {
            var name = variable.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                variables[name] = await ReadNodeAsync(Node(variable.Value.Trim()));
            }
        }

        foreach (var expression in node.Elements().Where(e => e.Name.LocalName == "Expression"))
        {
            var name = expression.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                variables[name] = GenApiFormula.Evaluate(expression.Value.Trim(), variables);
            }
        }

        return variables;
    }

    /// <summary>
    /// A register's address is the sum of every literal Address and every pAddress it references,
    /// plus an optional index stride — that is how the Genie Nano offsets a feature block per
    /// selector, and how it lays a feature's value, minimum and maximum out as three consecutive
    /// slots that differ only by a pIndex Offset.
    /// </summary>
    private async Task<uint> ResolveAddressAsync(XElement node)
    {
        double address = 0;
        foreach (var element in node.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "Address":
                    address += ParseLiteral(element.Value.Trim());
                    break;

                case "pAddress":
                    address += await ReadNodeAsync(Node(element.Value.Trim()));
                    break;

                case "pIndex":
                {
                    var index = await ReadNodeAsync(Node(element.Value.Trim()));
                    var offsetAttribute = element.Attribute("Offset")?.Value;
                    var offsetReference = element.Attribute("pOffset")?.Value;

                    var offset = offsetAttribute is not null
                        ? ParseLiteral(offsetAttribute)
                        : offsetReference is not null
                            ? await ReadNodeAsync(Node(offsetReference))
                            : 0;

                    address += index * offset;
                    break;
                }
            }
        }

        return (uint)address;
    }

    private async Task<double> ReadRegisterNodeAsync(XElement node)
    {
        var owner = RegisterOwner(node);
        var address = await ResolveAddressAsync(owner);
        var length = int.Parse(ChildText(owner, "Length") ?? "4", CultureInfo.InvariantCulture);
        var littleEndian = IsLittleEndian(node) && IsLittleEndian(owner);
        var raw = await _gvcp.ReadMemoryAsync(address, length);

        if (node.Name.LocalName == "FloatReg")
        {
            var bytes = littleEndian == BitConverter.IsLittleEndian ? raw : raw.Reverse().ToArray();
            return length == 8 ? BitConverter.ToDouble(bytes) : BitConverter.ToSingle(bytes);
        }

        var value = ToUnsigned(raw, littleEndian);

        if (node.Name.LocalName is "MaskedIntReg" or "StructEntry")
        {
            var (shift, mask) = MaskOf(node, length, littleEndian);
            value = (value >> shift) & mask;

            if (ChildText(node, "Sign") == "Signed")
            {
                var fieldSignBit = (mask + 1) >> 1;
                if (fieldSignBit != 0 && (value & fieldSignBit) != 0)
                {
                    return (double)(long)(value - (mask + 1));
                }
            }

            return value;
        }

        if (ChildText(node, "Sign") == "Signed")
        {
            var signBit = 1UL << (length * 8 - 1);
            if ((value & signBit) != 0)
            {
                return (double)(long)(value - (signBit << 1));
            }
        }

        return value;
    }

    /// <param name="skipIfUnchanged">
    /// Skip the register write when it would not change the stored bytes. This camera answers a
    /// write of the value a register already holds with GVCP status 0x8008, so re-applying an
    /// unchanged setting would otherwise surface as a hardware error. Commands must not use this:
    /// firing one is the point even when the register already reads the command value.
    /// </param>
    private async Task WriteNodeAsync(XElement node, double value, bool skipIfUnchanged = false)
    {
        switch (node.Name.LocalName)
        {
            case "Boolean":
            {
                var onValue = ChildText(node, "OnValue");
                var offValue = ChildText(node, "OffValue");
                var mapped = onValue is not null && offValue is not null
                    ? (value != 0 ? ParseLiteral(onValue) : ParseLiteral(offValue))
                    : value;

                await WriteNodeAsync(Reference(node, "pValue"), mapped, skipIfUnchanged);
                return;
            }

            case "Integer":
            case "Float":
            case "Enumeration":
            {
                // A node whose value is a literal rather than a pValue reference is fixed by the
                // camera's XML — TriggerSelector is one, hard-wired to FrameStart here.
                var target = ChildText(node, "pValue")
                    ?? throw new GenApiReadOnlyException(
                        $"'{node.Attribute("Name")?.Value}' bu kamerada sabit, değiştirilemez.");

                await WriteNodeAsync(Node(target), value, skipIfUnchanged);
                return;
            }

            case "IntConverter":
            case "Converter":
            {
                // FormulaTo turns this node's value, bound as FROM, into the linked node's value.
                var variables = await ReadVariablesAsync(node);
                variables["FROM"] = value;
                var converted = GenApiFormula.Evaluate(
                    ChildText(node, "FormulaTo") ?? throw new InvalidOperationException("Converter'da FormulaTo yok."),
                    variables);
                await WriteNodeAsync(Reference(node, "pValue"), converted, skipIfUnchanged);
                return;
            }

            case "IntReg":
            case "MaskedIntReg":
            case "StructEntry":
            case "FloatReg":
                await WriteRegisterNodeAsync(node, value, skipIfUnchanged);
                return;

            default:
                throw new NotSupportedException($"'{node.Name.LocalName}' düğüm tipine yazılamıyor.");
        }
    }

    private async Task WriteRegisterNodeAsync(XElement node, double value, bool skipIfUnchanged)
    {
        var owner = RegisterOwner(node);
        var address = await ResolveAddressAsync(owner);
        var length = int.Parse(ChildText(owner, "Length") ?? "4", CultureInfo.InvariantCulture);
        var littleEndian = IsLittleEndian(node) && IsLittleEndian(owner);

        byte[] bytes;
        if (node.Name.LocalName == "FloatReg")
        {
            bytes = length == 8 ? BitConverter.GetBytes(value) : BitConverter.GetBytes((float)value);
            if (littleEndian != BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
        }
        else
        {
            var raw = (ulong)(long)Math.Round(value, MidpointRounding.AwayFromZero);

            if (node.Name.LocalName is "MaskedIntReg" or "StructEntry")
            {
                // Only the node's bit field may change, so merge into what the register holds now.
                var current = ToUnsigned(await _gvcp.ReadMemoryAsync(address, length), littleEndian);
                var (shift, mask) = MaskOf(node, length, littleEndian);
                raw = (current & ~(mask << shift)) | ((raw & mask) << shift);
            }

            bytes = FromUnsigned(raw, length, littleEndian);
        }

        if (skipIfUnchanged)
        {
            var stored = await _gvcp.ReadMemoryAsync(address, length);
            if (bytes.SequenceEqual(stored))
            {
                return;
            }
        }

        await _gvcp.WriteMemoryAsync(address, bytes);
    }

    /// <summary>
    /// A StructEntry has no geometry of its own: address, length and byte order come from the
    /// StructReg it is declared inside.
    /// </summary>
    private static XElement RegisterOwner(XElement node) =>
        node.Name.LocalName == "StructEntry" ? node.Parent! : node;

    /// <summary>Bit field of a MaskedIntReg or StructEntry, as a shift and mask over the register.</summary>
    private static (int Shift, ulong Mask) MaskOf(XElement node, int length, bool littleEndian)
    {
        var bit = ChildText(node, "Bit");
        if (bit is not null)
        {
            return (int.Parse(bit, CultureInfo.InvariantCulture), 1UL);
        }

        var lsb = int.Parse(ChildText(node, "LSB") ?? "0", CultureInfo.InvariantCulture);
        var msb = int.Parse(ChildText(node, "MSB") ?? (length * 8 - 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        // Big-endian registers number bits from the most significant end, so LSB/MSB come back
        // reversed relative to the integer we assembled (GenICam §MaskedIntReg).
        if (!littleEndian)
        {
            var totalBits = length * 8;
            (lsb, msb) = (totalBits - 1 - lsb, totalBits - 1 - msb);
        }

        var width = msb - lsb + 1;
        return (lsb, width >= 64 ? ulong.MaxValue : (1UL << width) - 1);
    }

    private static bool IsLittleEndian(XElement node) => ChildText(node, "Endianess") != "BigEndian";

    private static ulong ToUnsigned(byte[] raw, bool littleEndian)
    {
        Span<byte> padded = stackalloc byte[8];
        if (littleEndian)
        {
            raw.AsSpan(0, Math.Min(8, raw.Length)).CopyTo(padded);
            return BinaryPrimitives.ReadUInt64LittleEndian(padded);
        }

        raw.AsSpan(0, Math.Min(8, raw.Length)).CopyTo(padded[(8 - Math.Min(8, raw.Length))..]);
        return BinaryPrimitives.ReadUInt64BigEndian(padded);
    }

    private static byte[] FromUnsigned(ulong value, int length, bool littleEndian)
    {
        Span<byte> full = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(full, value);
        var bytes = full[..length].ToArray();
        if (!littleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    private static string? ChildText(XElement node, string childName) =>
        node.Elements().FirstOrDefault(e => e.Name.LocalName == childName)?.Value.Trim();

    private static double ParseLiteral(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : double.Parse(text, CultureInfo.InvariantCulture);
}

/// <summary>A GenICam operation the camera refused, named by the feature it was about.</summary>
public sealed class GenApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A feature the camera's XML defines as a fixed value, so it cannot be written.</summary>
public sealed class GenApiReadOnlyException(string message) : Exception(message);

public sealed record FeatureRange(double? Min, double? Max, double? Increment)
{
    public static readonly FeatureRange Unbounded = new(null, null, null);

    public double Clamp(double value)
    {
        var clamped = value;
        if (Min is not null)
        {
            clamped = Math.Max(clamped, Min.Value);
        }

        if (Max is not null)
        {
            clamped = Math.Min(clamped, Max.Value);
        }

        return clamped;
    }
}
