using System;
using System.Runtime.InteropServices;

namespace UsnWatcher.Core
{
    /// <summary>
    /// A safe, disposable wrapper around a Win32 volume handle.
    ///
    /// Usage:
    ///   using var volume = VolumeHandle.Open("C");
    ///   // use volume.Handle
    /// </summary>
    public sealed class VolumeHandle : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        public IntPtr Handle => _disposed
            ? throw new ObjectDisposedException(nameof(VolumeHandle))
            : _handle;

        public string VolumeLetter { get; }

        private VolumeHandle(IntPtr handle, string volumeLetter)
        {
            _handle      = handle;
            VolumeLetter = volumeLetter;
        }

        /// <summary>
        /// Opens a volume handle for USN Journal access.
        /// Requires Administrator privileges.
        /// </summary>
        /// <param name="volumeLetter">Single drive letter, e.g. "C"</param>
        public static VolumeHandle Open(string volumeLetter)
        {
            volumeLetter = volumeLetter.TrimEnd(':', '\\').ToUpper();
            string volumePath = $@"\\.\{volumeLetter}:";

            IntPtr handle = NativeApi.CreateFile(
                volumePath,
                NativeApi.GENERIC_READ,
                NativeApi.FILE_SHARE_READ | NativeApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeApi.OPEN_EXISTING,
                0,
                IntPtr.Zero
            );

            NativeApi.CheckHandle(handle, $"CreateFile({volumePath})");
            return new VolumeHandle(handle, volumeLetter);
        }

        public void Dispose()
        {
            if (!_disposed && _handle != NativeApi.INVALID_HANDLE_VALUE)
            {
                NativeApi.CloseHandle(_handle);
                _handle  = IntPtr.Zero;
                _disposed = true;
            }
        }
    }
}
