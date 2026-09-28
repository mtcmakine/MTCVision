import { useEffect, useRef, useState } from 'react'
import { WS_BASE_URL } from '../api/client'

/**
 * Subscribes to the camera's live JPEG stream and hands back an object URL for the newest frame.
 *
 * One socket for the whole app: the backend broadcasts to every client, so opening a second one
 * would double the JPEG traffic for the same pictures. The previous frame's URL is revoked as the
 * next arrives, otherwise a long run leaks a blob per frame.
 */
export function useLiveStream(enabled: boolean): { imageUrl: string | null } {
  const [imageUrl, setImageUrl] = useState<string | null>(null)
  const currentUrlRef = useRef<string | null>(null)

  useEffect(() => {
    if (!enabled) {
      return
    }

    const socket = new WebSocket(`${WS_BASE_URL}/ws/live`)
    socket.binaryType = 'blob'
    socket.onmessage = (event) => {
      const url = URL.createObjectURL(event.data as Blob)
      if (currentUrlRef.current) {
        URL.revokeObjectURL(currentUrlRef.current)
      }
      currentUrlRef.current = url
      setImageUrl(url)
    }

    return () => {
      socket.close()
      if (currentUrlRef.current) {
        URL.revokeObjectURL(currentUrlRef.current)
        currentUrlRef.current = null
      }
      setImageUrl(null)
    }
  }, [enabled])

  return { imageUrl }
}
