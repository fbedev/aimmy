using System;
using System.Runtime.InteropServices;

namespace Aimmy.Mac
{
    public static class NativeMethods
    {
        private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        // --- Mouse Control ---

        public enum CGEventType : uint
        {
            LeftMouseDown = 1,
            LeftMouseUp = 2,
            MouseMoved = 5,
            LeftMouseDragged = 6
        }

        public enum CGMouseButton : uint
        {
            Left = 0,
            Right = 1,
            Center = 2
        }

        public enum CGEventTapLocation : uint
        {
            HIDEventTap = 0,
            SessionEventTap = 1,
            AnnotatedSessionEventTap = 2
        }

        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGEventCreateMouseEvent(
            IntPtr source, 
            CGEventType mouseType, 
            CGPoint mouseCursorPosition, 
            CGMouseButton mouseButton);

        [DllImport(CoreGraphicsLib)]
        public static extern void CGEventPost(
            CGEventTapLocation tap, 
            IntPtr Event);

        [DllImport(CoreGraphicsLib)]
        public static extern void CFRelease(IntPtr obj);

        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGEventCreate(IntPtr source);

        [DllImport(CoreGraphicsLib)]
        public static extern CGPoint CGEventGetLocation(IntPtr @event);

        [DllImport(CoreGraphicsLib)]
        public static extern bool CGEventSourceKeyState(int stateID, ushort key);

        public const int kCGEventSourceStateCombinedSessionState = 0;
        public const int kCGEventSourceStateHIDSystemState = 1;
        
        // Keycodes: 0x00 is 'A', but mouse buttons? 
        // CGEventSourceButtonState is separate function? Yes.
        
        [DllImport(CoreGraphicsLib)]
        public static extern bool CGEventSourceButtonState(int stateID, CGMouseButton button);

        [DllImport(CoreGraphicsLib)]
        public static extern void CGEventSetIntegerValueField(IntPtr @event, int field, long value);

        public const int kCGMouseEventDeltaX = 4;
        public const int kCGMouseEventDeltaY = 5;
        
        // --- Screen Capture ---
        
        [DllImport(CoreGraphicsLib)]
        public static extern uint CGMainDisplayID();
        
        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGDisplayCreateImage(uint displayID);
        
        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGDisplayCreateImageForRect(uint displayID, CGRect rect);

        [DllImport(CoreGraphicsLib)]
        public static extern long CGDisplayPixelsWide(uint displayID);

        [DllImport(CoreGraphicsLib)]
        public static extern long CGDisplayPixelsHigh(uint displayID);

        [DllImport(CoreGraphicsLib)]
        public static extern CGRect CGDisplayBounds(uint displayID);

        // --- Image Data Access ---
        
        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGImageGetDataProvider(IntPtr image);
        
        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGDataProviderCopyData(IntPtr provider);
        
        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CFDataGetBytePtr(IntPtr data);
        
        [DllImport(CoreGraphicsLib)]
        public static extern long CFDataGetLength(IntPtr data);
        
        [DllImport(CoreGraphicsLib)]
        public static extern int CGImageGetWidth(IntPtr image);
        
        [DllImport(CoreGraphicsLib)]
        public static extern int CGImageGetHeight(IntPtr image);

        [DllImport(CoreGraphicsLib)]
        public static extern int CGImageGetBytesPerRow(IntPtr image);

        [DllImport(CoreGraphicsLib)]
        public static extern int CGImageGetBitsPerPixel(IntPtr image);

        // --- Display List ---
        [DllImport(CoreGraphicsLib)]
        public static extern int CGGetActiveDisplayList(uint maxDisplays, [In, Out] uint[] activeDisplays, out uint displayCount);
        
        [DllImport(CoreGraphicsLib)]
        public static extern int CGGetOnlineDisplayList(uint maxDisplays, [In, Out] uint[] onlineDisplays, out uint displayCount);

        public enum CGError : int
        {
            Success = 0,
            Failure = 1000,
            IllegalArgument = 1001,
            InvalidConnection = 1002,
            InvalidContext = 1003,
            CannotComplete = 1004,
            NotImplemented = 1006,
            RangeCheck = 1007,
            TypeCheck = 1008,
            InvalidOperation = 1010,
            NoneAvailable = 1011
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint
    {
        public double X;
        public double Y;
        
        public CGPoint(double x, double y)
        {
            X = x; Y = y;
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize
    {
        public double Width;
        public double Height;
        
        public CGSize(double width, double height) 
        {
            Width = width; Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
        
        public CGRect(double x, double y, double width, double height)
        {
            Origin = new CGPoint(x, y);
            Size = new CGSize(width, height);
        }
    }

    public static class ObjCRuntime
    {
           [DllImport("/usr/lib/libobjc.A.dylib")]
        public static extern IntPtr objc_getClass(string name);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        public static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        public static extern void objc_msgSend(IntPtr self, IntPtr op, bool arg);
        
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint="objc_msgSend")]
        public static extern IntPtr objc_msgSend_IntPtr(IntPtr self, IntPtr op);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint="objc_msgSend")]
        public static extern void objc_msgSend_Long(IntPtr self, IntPtr op, long arg);
    }
    
    public static class CoreFoundation
    {
        private const string CFLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(CFLib)]
        public static extern int CFArrayGetCount(IntPtr theArray);

        [DllImport(CFLib)]
        public static extern IntPtr CFArrayGetValueAtIndex(IntPtr theArray, int idx);

        [DllImport(CFLib)]
        public static extern bool CFDictionaryGetValueIfPresent(IntPtr theDict, IntPtr key, out IntPtr value);
        
        [DllImport(CFLib)]
        public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, int encoding);
        
        [DllImport(CFLib)]
        public static extern bool CFStringGetCString(IntPtr theString, IntPtr buffer, int bufferSize, int encoding);
        
        [DllImport(CFLib)]
        public static extern bool CFNumberGetValue(IntPtr number, int theType, out int value);
        
        [DllImport(CFLib)]
        public static extern bool CFNumberGetValue(IntPtr number, int theType, out double value);

        [DllImport(CFLib)]
        public static extern void CFRelease(IntPtr cf);
        
        public const int kCFStringEncodingUTF8 = 0x08000100;
        public const int kCFNumberIntType = 9;
        public const int kCFNumberFloatType = 12;
        public const int kCFNumberCGFloatType = 16;
        
        // Helper to get string from CFString
        public static unsafe string? GetString(IntPtr cfString)
        {
            if (cfString == IntPtr.Zero) return null;
            
            // Try fast buffer
            byte[] buffer = new byte[256];
            fixed (byte* ptr = buffer)
            {
                if (CFStringGetCString(cfString, (IntPtr)ptr, 256, kCFStringEncodingUTF8))
                {
                    return  System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                }
            }
            return null; // Too long or failed
        }
    }

    public static class WindowList
    {
        private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGWindowListCopyWindowInfo(int option, uint relativeToWindow);
        
        public const int kCGWindowListOptionOnScreenOnly = (1 << 0);
        public const int kCGWindowListExcludeDesktopElements = (1 << 4);
    }
}
