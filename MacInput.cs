using System;

namespace Aimmy.Mac
{
    public static class MacInput
    {
        public static void MoveMouse(double x, double y)
        {
            var point = new CGPoint(x, y);
            
            // Create a MouseMoved event
            // Note: Key presses or clicks can be added similarly
            IntPtr moveEvent = NativeMethods.CGEventCreateMouseEvent(
                IntPtr.Zero, 
                NativeMethods.CGEventType.MouseMoved, 
                point, 
                NativeMethods.CGMouseButton.Left);

            if (moveEvent != IntPtr.Zero)
            {
                // Post event to session
                NativeMethods.CGEventPost(NativeMethods.CGEventTapLocation.SessionEventTap, moveEvent);
                NativeMethods.CFRelease(moveEvent);
            }
        }

        public static void MoveMouseRelative(int x, int y)
        {
             // Get current pos just to have a valid point
             IntPtr ev = NativeMethods.CGEventCreate(IntPtr.Zero);
             CGPoint currentPos = NativeMethods.CGEventGetLocation(ev);
             NativeMethods.CFRelease(ev);
             
             // Create MouseMoved event at current position (so visually it doesn't warp)
             IntPtr moveEvent = NativeMethods.CGEventCreateMouseEvent(
                IntPtr.Zero, 
                NativeMethods.CGEventType.MouseMoved, 
                currentPos, 
                NativeMethods.CGMouseButton.Left);

             if (moveEvent != IntPtr.Zero)
             {
                 // Inject Deltas
                 NativeMethods.CGEventSetIntegerValueField(moveEvent, NativeMethods.kCGMouseEventDeltaX, x);
                 NativeMethods.CGEventSetIntegerValueField(moveEvent, NativeMethods.kCGMouseEventDeltaY, y);
                 
                 NativeMethods.CGEventPost(NativeMethods.CGEventTapLocation.SessionEventTap, moveEvent);
                 NativeMethods.CFRelease(moveEvent);
             }
        }
        public static void SendClick(double x, double y)
        {
            var point = new CGPoint(x, y);

            IntPtr downEvent = NativeMethods.CGEventCreateMouseEvent(
                IntPtr.Zero, NativeMethods.CGEventType.LeftMouseDown, point, NativeMethods.CGMouseButton.Left);
            IntPtr upEvent = NativeMethods.CGEventCreateMouseEvent(
                IntPtr.Zero, NativeMethods.CGEventType.LeftMouseUp, point, NativeMethods.CGMouseButton.Left);

            if (downEvent != IntPtr.Zero && upEvent != IntPtr.Zero)
            {
                NativeMethods.CGEventPost(NativeMethods.CGEventTapLocation.SessionEventTap, downEvent);
                NativeMethods.CGEventPost(NativeMethods.CGEventTapLocation.SessionEventTap, upEvent);
                NativeMethods.CFRelease(downEvent);
                NativeMethods.CFRelease(upEvent);
            }
        }

        public static void SendLeftMouseDown(double x, double y)
        {
            var point = new CGPoint(x, y);
            IntPtr downEvent = NativeMethods.CGEventCreateMouseEvent(
                IntPtr.Zero, NativeMethods.CGEventType.LeftMouseDown, point, NativeMethods.CGMouseButton.Left);

            if (downEvent != IntPtr.Zero)
            {
                NativeMethods.CGEventPost(NativeMethods.CGEventTapLocation.SessionEventTap, downEvent);
                NativeMethods.CFRelease(downEvent);
            }
        }

        public static void SendLeftMouseUp(double x, double y)
        {
            var point = new CGPoint(x, y);
            IntPtr upEvent = NativeMethods.CGEventCreateMouseEvent(
                IntPtr.Zero, NativeMethods.CGEventType.LeftMouseUp, point, NativeMethods.CGMouseButton.Left);

            if (upEvent != IntPtr.Zero)
            {
                NativeMethods.CGEventPost(NativeMethods.CGEventTapLocation.SessionEventTap, upEvent);
                NativeMethods.CFRelease(upEvent);
            }
        }
    }
}
