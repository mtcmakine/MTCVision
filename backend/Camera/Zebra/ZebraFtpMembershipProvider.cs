using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FubarDev.FtpServer.AccountManagement;

namespace MTCVision.Backend.Camera.Zebra;

/// <summary>
/// Accepts the one username/password pair configured in <see cref="ZebraCameraOptions"/> (what was
/// typed into Aurora Focus's FTP settings), and still lets an anonymous login through so the
/// earlier "anonymous, blank password" setup keeps working.
/// </summary>
public sealed class ZebraFtpMembershipProvider(ZebraCameraOptions options) : IMembershipProviderAsync
{
    public Task<MemberValidationResult> ValidateUserAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.Equals(username, "anonymous", StringComparison.Ordinal))
        {
            return Task.FromResult(new MemberValidationResult(
                MemberValidationStatus.Anonymous, AnonymousMembershipProvider.CreateAnonymousPrincipal(password)));
        }

        if (!string.IsNullOrEmpty(options.FtpUsername)
            && FixedTimeEquals(username, options.FtpUsername)
            && FixedTimeEquals(password, options.FtpPassword))
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimsIdentity.DefaultNameClaimType, username),
                    new Claim(ClaimsIdentity.DefaultRoleClaimType, "user"),
                    new Claim(ClaimTypes.AuthenticationMethod, "password"),
                ],
                "password");
            return Task.FromResult(new MemberValidationResult(
                MemberValidationStatus.AuthenticatedUser, new ClaimsPrincipal(identity)));
        }

        return Task.FromResult(new MemberValidationResult(MemberValidationStatus.InvalidLogin));
    }

    public Task LogOutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) => Task.CompletedTask;

#pragma warning disable CS0618 // The interface still declares the pre-async overload.
    public Task<MemberValidationResult> ValidateUserAsync(string username, string password) =>
        ValidateUserAsync(username, password, CancellationToken.None);
#pragma warning restore CS0618

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}
