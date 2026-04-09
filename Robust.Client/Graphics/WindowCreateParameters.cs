using System;

namespace Robust.Client.Graphics
{
    public sealed class WindowCreateParameters
    {
        public int Width = 1280;
        public int Height = 720;
        public string Title = "";
        public bool Maximized;
        public bool Visible = true;
        public IClydeMonitor? Monitor;
        public bool Fullscreen;

        /// <summary>
        /// The window that will "own" this window.
        /// Owned windows always appear on top of their owners and have some other misc behavior depending on the OS.
        /// </summary>
        public IClydeWindow? Owner;

        /// <summary>
        /// Controls where a window is initially placed when created.
        /// </summary>
        public WindowStartupLocation StartupLocation;

        /// <summary>
        /// Specifies window styling options for the created window.
        /// </summary>
        public OSWindowStyles Styles;

        /// <summary>
        /// On Windows, if set, RT will wrap this existing HWND instead of creating a new
        /// top level window. Ownership of the HWND stays with the caller. This is used
        /// for embedding RT inside another application (e.g. a WPF host that renders
        /// RT content into an <c>HwndHost</c> child).
        /// </summary>
        public IntPtr? ExternalHWnd;

        /// <summary>
        /// If false, RT will not destroy the window on shutdown. This is required when
        /// <see cref="ExternalHWnd"/> is set so RT does not tear down a window it does
        /// not own. Default is <c>true</c> (RT owns the window it created).
        /// </summary>
        public bool WindowOwned = true;

        /// <summary>
        /// If true, the caller has already established a graphics context for the
        /// window and RT should skip its own GL context creation. Reserved for future
        /// use by embedding hosts that want to share a context with another renderer.
        /// Leave false for now.
        /// </summary>
        public bool UseExternalGraphicsContext;
    }

    /// <summary>
    /// Controls where a window is initially placed when created.
    /// </summary>
    public enum WindowStartupLocation : byte
    {
        /// <summary>
        /// The window position is automatically picked by the windowing system.
        /// </summary>
        Manual,

        /// <summary>
        /// The window is positioned at the center of the <see cref="WindowCreateParameters.Owner"/> window.
        /// </summary>
        CenterOwner,
    }

    /// <summary>
    /// Specifies window styling options for an OS window.
    /// </summary>
    [Flags]
    public enum OSWindowStyles
    {
        /// <summary>
        /// No special styles set.
        /// </summary>
        None = 0,

        /// <summary>
        /// Hide title buttons such as close and minimize.
        /// </summary>
        NoTitleOptions = 1 << 0,

        /// <summary>
        /// Completely hide the title bar
        /// </summary>
        NoTitleBar = 1 << 1,
    }
}
