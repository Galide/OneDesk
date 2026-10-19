using System.Runtime.InteropServices;

namespace OneDesk.Client;

public static class MacNative
{
    public const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint
    {
        public double X;
        public double Y;

        public CGPoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    public enum CGEventTapLocation
    {
        HID = 0,
        Session = 1,
        AnnotatedSession = 2
    }

    public enum CGEventType
    {
        LeftMouseDown = 1,
        LeftMouseUp = 2,
        RightMouseDown = 3,
        RightMouseUp = 4,
        MouseMoved = 5,
        LeftMouseDragged = 6,
        RightMouseDragged = 7,
        ScrollWheel = 22,
        OtherMouseDown = 25,
        OtherMouseUp = 26,
        OtherMouseDragged = 27
    }

    public enum CGMouseButton
    {
        Left = 0,
        Right = 1,
        Center = 2
    }

    public enum CGScrollEventUnit
    {
        Pixel = 0,
        Line = 1
    }

    public enum CGEventSourceStateID
    {
        Private = -1,
        CombinedSession = 0,
        HIDSystem = 1
    }

    public enum CGEventField
    {
        KeyboardEventAutorepeat = 8
    }

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey,
        [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateMouseEvent(IntPtr source, CGEventType mouseType,
        CGPoint mouseCursorPosition, CGMouseButton mouseButton);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, CGScrollEventUnit units,
        uint wheelCount, int wheel1, int wheel2, int wheel3);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetFlags(IntPtr @event, ulong flags);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetIntegerValueField(IntPtr @event, CGEventField field, long value);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetLocation(IntPtr @event, CGPoint location);

    [DllImport(CoreGraphics)]
    public static extern void CGEventPost(CGEventTapLocation tap, IntPtr @event);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventSourceCreate(CGEventSourceStateID stateId);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGPreflightPostEventAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRequestPostEventAccess();

    [DllImport(CoreGraphics)]
    public static extern int CGWarpMouseCursorPosition(CGPoint newCursorPosition);

    [DllImport(CoreGraphics)]
    public static extern int CGAssociateMouseAndMouseCursorPosition(int connected);

    [DllImport(CoreGraphics)]
    public static extern int CGGetActiveDisplayList(uint maxDisplays, uint[] activeDisplays, out uint displayCount);

    [DllImport(CoreGraphics)]
    public static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    public static extern uint CGDisplayIsBuiltin(uint display);

    [DllImport(CoreGraphics)]
    public static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGDisplayCreateUUIDFromDisplayID(uint display);

    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFUUIDCreateString(IntPtr allocator, IntPtr uuid);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, uint encoding);

    public const uint CFStringEncodingUtf8 = 0x08000100;
}
