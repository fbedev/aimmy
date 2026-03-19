using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Aimmy.Mac
{
    public struct WindowInfo
    {
        public int ID;
        public string Name;
        public string OwnerName;
        public CGRect Bounds;
    }

    public static class WindowHelper
    {
        public static List<WindowInfo> GetWindows()
        {
            var list = new List<WindowInfo>();
            
            // Get Window List
            IntPtr array = WindowList.CGWindowListCopyWindowInfo(
                WindowList.kCGWindowListOptionOnScreenOnly | WindowList.kCGWindowListExcludeDesktopElements, 
                0);
                
            if (array == IntPtr.Zero) return list;
            
            try
            {
                int count = CoreFoundation.CFArrayGetCount(array);
                
                // Keys to look for
                IntPtr kOwnerName = CreateCFString("kCGWindowOwnerName");
                IntPtr kName = CreateCFString("kCGWindowName");
                IntPtr kBounds = CreateCFString("kCGWindowBounds");
                IntPtr kID = CreateCFString("kCGWindowNumber");
                
                // Bounds Keys
                IntPtr kX = CreateCFString("X");
                IntPtr kY = CreateCFString("Y");
                IntPtr kW = CreateCFString("Width");
                IntPtr kH = CreateCFString("Height");

                for (int i = 0; i < count; i++)
                {
                    IntPtr dict = CoreFoundation.CFArrayGetValueAtIndex(array, i);
                    if (dict == IntPtr.Zero) continue;
                    
                    WindowInfo info = new WindowInfo();
                    
                    // ID
                    if (CoreFoundation.CFDictionaryGetValueIfPresent(dict, kID, out IntPtr idVal))
                    {
                        CoreFoundation.CFNumberGetValue(idVal, CoreFoundation.kCFNumberIntType, out info.ID);
                    }
                    
                    // Owner
                    if (CoreFoundation.CFDictionaryGetValueIfPresent(dict, kOwnerName, out IntPtr ownerVal))
                    {
                        info.OwnerName = CoreFoundation.GetString(ownerVal) ?? "Unknown";
                    }
                    
                    // Name
                    if (CoreFoundation.CFDictionaryGetValueIfPresent(dict, kName, out IntPtr nameVal))
                    {
                        info.Name = CoreFoundation.GetString(nameVal) ?? "";
                    }
                    
                    // Bounds (Dictionary)
                    if (CoreFoundation.CFDictionaryGetValueIfPresent(dict, kBounds, out IntPtr errorVal)) // actually bounds dict
                    {
                        IntPtr boundsDict = errorVal;
                        
                        // Parse X, Y, W, H
                        double x = 0, y = 0, w = 0, h = 0;
                        
                        if (CoreFoundation.CFDictionaryGetValueIfPresent(boundsDict, kX, out IntPtr xv)) 
                            CoreFoundation.CFNumberGetValue(xv, CoreFoundation.kCFNumberCGFloatType, out x);

                        if (CoreFoundation.CFDictionaryGetValueIfPresent(boundsDict, kY, out IntPtr yv)) 
                            CoreFoundation.CFNumberGetValue(yv, CoreFoundation.kCFNumberCGFloatType, out y);
                            
                        if (CoreFoundation.CFDictionaryGetValueIfPresent(boundsDict, kW, out IntPtr wv)) 
                            CoreFoundation.CFNumberGetValue(wv, CoreFoundation.kCFNumberCGFloatType, out w);
                            
                        if (CoreFoundation.CFDictionaryGetValueIfPresent(boundsDict, kH, out IntPtr hv)) 
                            CoreFoundation.CFNumberGetValue(hv, CoreFoundation.kCFNumberCGFloatType, out h);
                            
                        info.Bounds = new CGRect(x, y, w, h);
                    }

                    // Filter out tiny windows or empty names if desired, but for now include all reasonable ones
                    if (info.Bounds.Size.Width > 10 && info.Bounds.Size.Height > 10)
                    {
                        list.Add(info);
                    }
                }
                
                CoreFoundation.CFRelease(kOwnerName);
                CoreFoundation.CFRelease(kName);
                CoreFoundation.CFRelease(kBounds);
                CoreFoundation.CFRelease(kID);
                CoreFoundation.CFRelease(kX);
                CoreFoundation.CFRelease(kY);
                CoreFoundation.CFRelease(kW);
                CoreFoundation.CFRelease(kH);
            }
            finally
            {
                CoreFoundation.CFRelease(array);
            }
            
            return list;
        }
        
        static IntPtr CreateCFString(string str)
        {
            return CoreFoundation.CFStringCreateWithCString(IntPtr.Zero, str, CoreFoundation.kCFStringEncodingUTF8);
        }
    }
}
