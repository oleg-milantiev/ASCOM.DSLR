﻿using ASCOM.DSLR.Enums;
using ASCOM.DSLR.Native;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace ASCOM.DSLR.Classes
{
    public class ImageDataProcessor
    {
        private IntPtr LoadRaw(string fileName)
        {
            IntPtr data = NativeMethods.libraw_init(LibRaw_constructor_flags.LIBRAW_OPIONS_NO_DATAERR_CALLBACK);
            NativeMethods.libraw_open_file(data, fileName);
            NativeMethods.libraw_unpack(data);
            NativeMethods.libraw_raw2image(data);
            NativeMethods.libraw_subtract_black(data);

            return data;
        }

        public int[,,] ReadAndDebayerRaw(string fileName)
        {
            IntPtr data = LoadRaw(fileName);
            NativeMethods.libraw_dcraw_process(data);

            var dataStructure = GetStructure<libraw_data_t>(data);
            ushort width = dataStructure.sizes.iwidth;
            ushort height = dataStructure.sizes.iheight;

            var pixels = new int[width, height, 3];

            for (int rc = 0; rc < height * width; rc++)
            {
                var r = (ushort)Marshal.ReadInt16(dataStructure.image, rc * 8);
                var g = (ushort)Marshal.ReadInt16(dataStructure.image, rc * 8 + 2);
                var b = (ushort)Marshal.ReadInt16(dataStructure.image, rc * 8 + 4);

                int row = rc / width;
                int col = rc - width * row;
                int rowReversed = height - row - 1;
                pixels[col, rowReversed, 0] = b;
                pixels[col, rowReversed, 1] = g;
                pixels[col, rowReversed, 2] = r;
            }
            NativeMethods.libraw_close(data);

            return pixels;
        }

        public int[,,] ReadJpeg(string fileName)
        {
            Bitmap img = new Bitmap(fileName);
            BitmapData data = img.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.ReadOnly, img.PixelFormat);
            IntPtr ptr = data.Scan0;
            int bytesCount = Math.Abs(data.Stride) * img.Height;
            var result = new int[img.Width, img.Height, 3];

            byte[] bytesArray = new byte[bytesCount];
            Marshal.Copy(ptr, bytesArray, 0, bytesCount);
            img.UnlockBits(data);

            var width = img.Width;
            var height = img.Height;

            for (int rc = 0; rc < width * height; rc++)
            {
                var r = bytesArray[rc * 3];
                var g = bytesArray[rc * 3 + 1];
                var b = bytesArray[rc * 3 + 2];

                int row = rc / width;
                int col = rc - width * row;

                var rowReversed = height - row - 1;
                result[col, rowReversed, 0] = b;
                result[col, rowReversed, 1] = g;
                result[col, rowReversed, 2] = r;
            }

            return result;
        }

        private void exif_parser_callback(IntPtr context, int tag, int type, int len, uint ord, IntPtr ifp)
        {

        }

        public int[,] ReadRaw(string fileName)
        {
            IntPtr data = LoadRaw(fileName);

            var dataStructure = GetStructure<libraw_data_t>(data);


            var pixels = new int[dataStructure.sizes.iwidth, dataStructure.sizes.iheight];
            ushort width = dataStructure.sizes.iwidth;
            ushort height = dataStructure.sizes.iheight;

            for (int rc = 0; rc < width * height; rc++)
            {
                var r = (ushort)Marshal.ReadInt16(dataStructure.image, rc * 8);
                var g = (ushort)Marshal.ReadInt16(dataStructure.image, rc * 8 + 2);
                var b = (ushort)Marshal.ReadInt16(dataStructure.image, rc * 8 + 4);
                var g2 = (ushort)Marshal.ReadInt16(dataStructure.image, rc * 8 + 6);
                
                int row = rc / width;
                int col = rc - width * row;
          
                if (row % 2 == 0 && col % 2 == 0)
                {
                    pixels[col, row] = r;
                }
                else if (row % 2 == 0 && col % 2 == 1)
                {
                    pixels[col, row] = g;
                }
                else if (row % 2 == 1 && col % 2 == 0)
                {
                    pixels[col, row] = g2;
                }
                else if (row % 2 == 1 && col % 2 == 1)
                {
                    pixels[col, row] = b;
                }

            }
            NativeMethods.libraw_close(data);

            return pixels;
        }

        

        public Array Binning(Array data, int binx, int biny, BinningMode binningMode)
        {
            int width = data.GetLength(0);
            int height = data.GetLength(1);
            int binWidth = width / binx;
            int binHeight = height / biny;

            var result = Array.CreateInstance(typeof(int), binWidth, binHeight);

            for (int x = 0; x < binWidth; x++)
                for (int y = 0; y < binHeight; y++)
                {
                    var binBlockData = new List<int>();
                    for (int x2 = x * binx; x2 < x * binx + binx; x2++)
                        for (int y2 = y * biny; y2 < y * biny + biny; y2++)
                        {
                            binBlockData.Add((int)data.GetValue(x2, y2));
                        }

                    int value = 0;
                    switch (binningMode)
                    {
                        case BinningMode.Sum:
                            value = GetSum(binBlockData, binx, biny);
                            break;
                        case BinningMode.Median:
                            value = GetMedian(binBlockData);
                            break;
                    }
                    result.SetValue(value, x, y);
                }

            return result;
        }

        public Array CutArray(Array data, int StartX, int StartY, int NumX, int NumY, int CameraXSize, int CameraYSize)
        {
            Array result = null;
            int rank = data.Rank;

            if (IsCutRequired(data.GetLength(0), data.GetLength(1), StartX, StartY, NumX, NumY, CameraXSize, CameraYSize))
            {
                int startXCorrected = StartX % 2 == 0 ? StartX : StartX - 1;
                int startYCorrected = StartY % 2 == 0 ? StartY : StartY - 1;

                result = rank == 3 ? Array.CreateInstance(typeof(int), NumX, NumY, 3)
                                   : Array.CreateInstance(typeof(int), NumX, NumY);

                for (int x = 0; x < NumX; x++)
                    for (int y = 0; y < NumY; y++)
                    {
                        int dataX = startXCorrected + x;
                        int dataY = startYCorrected + y;
                        if (rank == 3)
                        {
                            for (int r = 0; r < 3; r++)
                            {
                                result.SetValue(data.GetValue(dataX, dataY, r), x, y, r);
                            }
                        }
                        else
                        {
                            result.SetValue(data.GetValue(dataX, dataY), x, y);
                        }
                    }
            }
            else
            {
                result = data;
            }
            return result;
        }

        private bool IsCutRequired(int dataXsize, int dataYsize, int StartX, int StartY, int NumX, int NumY, int CameraXSize, int CameraYSize)
        {
            bool sizeMatches = StartX == 0 && StartY == 0 && NumX == CameraXSize && NumY == CameraYSize
                && dataXsize == CameraXSize && dataYsize == CameraYSize;

            bool cut = !(sizeMatches || NumX == 0 || NumY == 0);
            return cut;
        }

        private int GetMedian(IEnumerable<int> sourceNumbers)
        {
            int[] sortedPNumbers = sourceNumbers.OrderBy(n => n).ToArray();

            int size = sortedPNumbers.Length;
            int mid = size / 2;
            int median = (size % 2 != 0) ? sortedPNumbers[mid] : (sortedPNumbers[mid] + sortedPNumbers[mid - 1]) / 2;
            return median;
        }

        private int GetSum(IEnumerable<int> sourceNumbers, int binx, int biny)
        {
            int binCount = binx * biny;
            var sum = sourceNumbers.Sum();

            if (binCount > 4)
            {
                sum = sum >> 2;
            }

            return sum;
        }

        private T GetStructure<T>(IntPtr ptr)
       where T : struct
        {
            return (T)Marshal.PtrToStructure(ptr, typeof(T));
        }
    }
}
