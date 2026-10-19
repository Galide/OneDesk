using System.Runtime.InteropServices;

namespace OneDesk.Client;

public static class MacNative
{
    public const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

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

    public enum CGEventTapLocation : uint
    {
        HID = 0,
        Session = 1,
        AnnotatedSession = 2
    }

    public enum CGEventTapPlacement : uint
    {
        HeadInsert = 0,
        TailAppend = 1
    }

    public enum CGEventTapOptions : uint
    {
        Default = 0,
        ListenOnly = 1
    }

    public enum CGEventType : uint
    {
        Null = 0,
        LeftMouseDown = 1,
        LeftMouseUp = 2,
        RightMouseDown = 3,
        RightMouseUp = 4,
        MouseMoved = 5,
        LeftMouseDragged = 6,
        RightMouseDragged = 7,
        KeyDown = 10,
        KeyUp = 11,
        FlagsChanged = 12,
        ScrollWheel = 22,
        OtherMouseDown = 25,
        OtherMouseUp = 26,
        OtherMouseDragged = 27,

        /// <summary>The system disabled the tap because our callback was too slow.</summary>
        TapDisabledByTimeout = 0xFFFFFFFE,

        /// <summary>The user disabled the tap (for example with a secure-input field).</summary>
        TapDisabledByUserInput = 0xFFFFFFFF
    }

    public enum CGMouseButton : uint
    {
        Left = 0,
        Right = 1,
        Center = 2
    }

    public enum CGScrollEventUnit : uint
    {
        Pixel = 0,
        Line = 1
    }

    public enum CGEventSourceStateID : int
    {
        Private = -1,
        CombinedSession = 0,
        HIDSystem = 1
    }

    public enum CGEventField : uint
    {
        MouseEventButtonNumber = 3,
        KeyboardEventAutorepeat = 8,
        KeyboardEventKeycode = 9,
        ScrollWheelEventDeltaAxis1 = 11,
        ScrollWheelEventDeltaAxis2 = 12,
        ScrollWheelEventIsContinuous = 88,
        ScrollWheelEventPointDeltaAxis1 = 96,
        ScrollWheelEventPointDeltaAxis2 = 97
    }

    /// <summary>CGEventFlags masks (CGEventTypes.h).</summary>
    public const ulong FlagAlphaShift = 0x00010000;
    public const ulong FlagShift = 0x00020000;
    public const ulong FlagControl = 0x00040000;
    public const ulong FlagAlternate = 0x00080000;
    public const ulong FlagCommand = 0x00100000;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr CGEventTapCallback(IntPtr proxy, uint type, IntPtr @event, IntPtr userInfo);

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
    public static extern ulong CGEventGetFlags(IntPtr @event);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetIntegerValueField(IntPtr @event, CGEventField field, long value);

    [DllImport(CoreGraphics)]
    public static extern long CGEventGetIntegerValueField(IntPtr @event, CGEventField field);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetLocation(IntPtr @event, CGPoint location);

    [DllImport(CoreGraphics)]
    public static extern CGPoint CGEventGetLocation(IntPtr @event);

    [DllImport(CoreGraphics)]
    public static extern void CGEventPost(CGEventTapLocation tap, IntPtr @event);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventSourceCreate(CGEventSourceStateID stateId);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventTapCreate(CGEventTapLocation tap, CGEventTapPlacement place,
        CGEventTapOptions options, ulong eventsOfInterest, IntPtr callback, IntPtr userInfo);

    [DllImport(CoreGraphics)]
    public static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGEventTapIsEnabled(IntPtr tap);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, IntPtr order);

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopRemoveSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    public static extern int CFRunLoopRunInMode(IntPtr mode, double seconds,
        [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopStop(IntPtr runLoop);

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopWakeUp(IntPtr runLoop);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGPreflightPostEventAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CGRequestPostEventAccess();

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AXIsProcessTrusted();

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
