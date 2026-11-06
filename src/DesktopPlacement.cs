using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace AMLyrics {
 internal static class DesktopPlacement {
  [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
  class DesktopManagerClass {}
  [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IDesktopManager {
   [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr window, [MarshalAs(UnmanagedType.Bool)] out bool current);
   [PreserveSig] int GetWindowDesktopId(IntPtr window, out Guid desktop);
   [PreserveSig] int MoveWindowToDesktop(IntPtr window, ref Guid desktop);
  }
  public static Guid Locate(Window window, out bool onCurrentDesktop) {
   object instance = new DesktopManagerClass();
   try {
    var manager = (IDesktopManager)instance;
    var handle = new WindowInteropHelper(window).EnsureHandle();
    Guid desktop;
    Marshal.ThrowExceptionForHR(manager.GetWindowDesktopId(handle, out desktop));
    Marshal.ThrowExceptionForHR(manager.IsWindowOnCurrentVirtualDesktop(handle, out onCurrentDesktop));
    return desktop;
   } finally { Marshal.ReleaseComObject(instance); }
  }
  public static void MoveOwnWindow(Window window, Guid target) {
   object instance = new DesktopManagerClass();
   try {
    var manager = (IDesktopManager)instance;
    var handle = new WindowInteropHelper(window).EnsureHandle();
    Marshal.ThrowExceptionForHR(manager.MoveWindowToDesktop(handle, ref target));
    Guid actual;
    Marshal.ThrowExceptionForHR(manager.GetWindowDesktopId(handle, out actual));
    if (actual != target) throw new InvalidOperationException("Window did not reach the requested desktop.");
   } finally { Marshal.ReleaseComObject(instance); }
  }
 }
}
