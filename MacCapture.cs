using System;
using System.Runtime.InteropServices;
using System.Diagnostics;

using System.Threading.Tasks;

namespace Aimmy.Mac
{
    public static class MacCapture
    {
        private static float[] _lut = CreateLut();
        
        static float[] CreateLut()
        {
             var l = new float[256];
             for(int i=0; i<256; i++) l[i] = i/255f;
             return l;
        }

        public static unsafe bool CaptureAndFillTensor(float[] tensorBuffer, int imageSize, CGRect rect, uint displayID)
        {
            if (tensorBuffer.Length != 3 * imageSize * imageSize) return false;
            
            // 2. Capture
            // Use provided display ID, fallback to main if 0 (though 0 is technically invalid CGDirectDisplayID? No, distinct from index)
            // CGMainDisplayID() returns the ID. 0 is kCGNullDirectDisplay.
            if (displayID == 0) displayID = NativeMethods.CGMainDisplayID();
            
            // Console.WriteLine($"Debug: DisplayID: {displayID}"); 

            IntPtr cgImage = NativeMethods.CGDisplayCreateImageForRect(displayID, rect);
            
            if (cgImage == IntPtr.Zero)
            {
                // Console.WriteLine($"Debug: Capture Rect {rect.Origin.X},{rect.Origin.Y} {rect.Size.Width}x{rect.Size.Height} failed.");
                // Fallback: Try Full Screen on THAT display
                cgImage = NativeMethods.CGDisplayCreateImage(displayID);
                
                if (cgImage == IntPtr.Zero) 
                {
                    Console.WriteLine("[MacCapture] Full screen capture failed. PERMISSION ISSUE LIKELY.");
                    return false;
                }
            }

            try 
            {
                // 3. Get Data
                int width = NativeMethods.CGImageGetWidth(cgImage);
                int height = NativeMethods.CGImageGetHeight(cgImage);
                
                // Check if image is smaller than expected
                if (width < imageSize || height < imageSize)
                {
                    // Console.WriteLine($"[MacCapture] Image Too Small! Expected {imageSize}, Got {width}x{height}.");
                    return false;
                }

                int cropX = (width - imageSize) / 2;
                int cropY = (height - imageSize) / 2;

                IntPtr dataProvider = NativeMethods.CGImageGetDataProvider(cgImage);
                IntPtr data = NativeMethods.CGDataProviderCopyData(dataProvider);
                
                try
                {
                    byte* ptr = (byte*)NativeMethods.CFDataGetBytePtr(data);
                    
                    int bpr = NativeMethods.CGImageGetBytesPerRow(cgImage);
                    int bytesPerPixel = NativeMethods.CGImageGetBitsPerPixel(cgImage) / 8;
                    
                    // 4. Fill Tensor
                    // Planar RGB: RRR... GGG... BBB...
                    int totalPixels = imageSize * imageSize; // Tensor size
                    int rOff = 0;
                    int gOff = totalPixels;
                    int bOff = totalPixels * 2;
                    
                    // Constant Factor for 1/255
                    float norm = 1.0f / 255.0f;
                    
                    for (int r = 0; r < imageSize; r++)
                    {
                        // Source Row
                        int srcRow = r + cropY;
                        byte* rowPtr = ptr + (srcRow * bpr);
                        
                        // Dest Row start
                        int destRowStart = r * imageSize;

                        for (int c = 0; c < imageSize; c++)
                        {
                            int srcCol = c + cropX;
                            byte* pix = rowPtr + (srcCol * bytesPerPixel);
                            
                            int idx = destRowStart + c;
                            
                            // Map: B G R A (Little Endian standard)
                            // Direct math is faster than array lookup for modern CPUs (ILP)
                            // And avoids cache pressure of LUT
                            tensorBuffer[rOff + idx] = pix[2] * norm;
                            tensorBuffer[gOff + idx] = pix[1] * norm;
                            tensorBuffer[bOff + idx] = pix[0] * norm;
                        }
                    }
                }
                finally
                {
                    NativeMethods.CFRelease(data);
                }
            }
            finally
            {
                NativeMethods.CFRelease(cgImage);
            }
            
            return true;
        }
    }
}
