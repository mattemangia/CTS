using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace CTSegmenter
{
    /// <summary>
    /// Provides various fusion strategies for segmentation volumes:
    /// - Majority voting,
    /// - Weighted averaging,
    /// - Probability map generation,
    /// - And a simple CRF-based (mean-field style) smoothing.
    /// 
    /// Assumes that input segmentation maps are represented as Bitmaps (24bpp RGB where R=G=B).
    /// For CRF, the input probability map should be a grayscale Bitmap with pixel values in 0–255.
    /// </summary>
    public static class CTFusion
    {
        /// <summary>
        /// Fuse segmentation maps using majority voting.
        /// For each pixel, the label is the one that occurs most frequently among the inputs.
        /// </summary>
        public static Bitmap MajorityVotingFusion(List<Bitmap> segmentationMaps)
        {
            if (segmentationMaps == null || segmentationMaps.Count == 0)
                throw new ArgumentException("No segmentation maps provided.");

            int width = segmentationMaps[0].Width;
            int height = segmentationMaps[0].Height;
            Bitmap fused = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            // Use LockBits for speed
            BitmapData fusedData = fused.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, fused.PixelFormat);
            int stride = fusedData.Stride;
            unsafe
            {
                byte* fusedPtr = (byte*)fusedData.Scan0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Count frequencies for this pixel
                        Dictionary<byte, int> counts = new Dictionary<byte, int>();
                        foreach (Bitmap bmp in segmentationMaps)
                        {
                            Color c = bmp.GetPixel(x, y); // for simplicity; for speed, use LockBits on each input too
                            byte label = c.R;
                            if (!counts.ContainsKey(label))
                                counts[label] = 0;
                            counts[label]++;
                        }
                        byte finalLabel = counts.OrderByDescending(kvp => kvp.Value).First().Key;
                        int offset = y * stride + x * 3;
                        fusedPtr[offset] = finalLabel;
                        fusedPtr[offset + 1] = finalLabel;
                        fusedPtr[offset + 2] = finalLabel;
                    }
                }
            }
            fused.UnlockBits(fusedData);
            return fused;
        }

        /// <summary>
        /// Fuse probability maps using weighted averaging.
        /// Each input is assumed to be a grayscale Bitmap (values 0–255 represent probabilities).
        /// If weights are not provided, equal weighting is assumed.
        /// </summary>
        public static Bitmap WeightedAveragingFusion(List<Bitmap> probabilityMaps, List<double> weights = null)
        {
            if (probabilityMaps == null || probabilityMaps.Count == 0)
                throw new ArgumentException("No probability maps provided.");

            int width = probabilityMaps[0].Width;
            int height = probabilityMaps[0].Height;
            Bitmap fused = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            int n = probabilityMaps.Count;
            if (weights == null || weights.Count != n)
            {
                weights = Enumerable.Repeat(1.0, n).ToList();
            }
            double totalWeight = weights.Sum();

            // Use LockBits for output, but for simplicity here we use GetPixel on inputs.
            BitmapData fusedData = fused.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, fused.PixelFormat);
            int stride = fusedData.Stride;
            unsafe
            {
                byte* fusedPtr = (byte*)fusedData.Scan0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        double sum = 0;
                        for (int i = 0; i < n; i++)
                        {
                            Color c = probabilityMaps[i].GetPixel(x, y);
                            double prob = c.R / 255.0;
                            sum += prob * weights[i];
                        }
                        double avgProb = sum / totalWeight;
                        byte fusedValue = (byte)(avgProb * 255);
                        int offset = y * stride + x * 3;
                        fusedPtr[offset] = fusedValue;
                        fusedPtr[offset + 1] = fusedValue;
                        fusedPtr[offset + 2] = fusedValue;
                    }
                }
            }
            fused.UnlockBits(fusedData);
            return fused;
        }

        /// <summary>
        /// Generate a probability map for a target label by computing frequency across segmentation maps.
        /// </summary>
        public static Bitmap ProbabilityMapFusion(List<Bitmap> segmentationMaps, byte targetLabel = 1)
        {
            if (segmentationMaps == null || segmentationMaps.Count == 0)
                throw new ArgumentException("No segmentation maps provided.");

            int width = segmentationMaps[0].Width;
            int height = segmentationMaps[0].Height;
            Bitmap probMap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            int n = segmentationMaps.Count;

            BitmapData probData = probMap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, probMap.PixelFormat);
            int stride = probData.Stride;
            unsafe
            {
                byte* ptr = (byte*)probData.Scan0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int count = 0;
                        foreach (Bitmap bmp in segmentationMaps)
                        {
                            Color c = bmp.GetPixel(x, y);
                            if (c.R == targetLabel)
                                count++;
                        }
                        double probability = (double)count / n;
                        byte intensity = (byte)(probability * 255);
                        int offset = y * stride + x * 3;
                        ptr[offset] = intensity;
                        ptr[offset + 1] = intensity;
                        ptr[offset + 2] = intensity;
                    }
                }
            }
            probMap.UnlockBits(probData);
            return probMap;
        }

        /// <summary>
        /// Applies a simple CRF smoothing to a probability map.
        /// This method uses a mean-field style update over a 4-neighborhood.
        /// It uses LockBits for fast pixel access.
        /// Inputs:
        ///   - probabilityMap: a grayscale Bitmap (24bpp, R=G=B) with values in 0–255.
        ///   - originalImage: the corresponding original grayscale image (for appearance cues).
        /// Hyperparameters:
        ///   - iterations: number of iterations.
        ///   - lambda: weight for the pairwise term.
        ///   - sigma: controls sensitivity to intensity differences.
        /// </summary>
        public static Bitmap CRFFusion(Bitmap probabilityMap, Bitmap originalImage, int iterations = 5, double lambda = 1.0, double sigma = 15.0)
        {
            if (probabilityMap == null || originalImage == null)
                throw new ArgumentNullException("Both probabilityMap and originalImage must be provided.");

            int width = probabilityMap.Width;
            int height = probabilityMap.Height;
            // We assume both images have the same dimensions.
            // Convert probability map to float[,] in [0,1].
            float[,] prob = new float[width, height];
            float[,] unary = new float[width, height];
            // Also extract the original intensity as float[,] (0 to 1)
            float[,] intensity = new float[width, height];

            // Use LockBits on both Bitmaps.
            BitmapData probData = probabilityMap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, probabilityMap.PixelFormat);
            BitmapData origData = originalImage.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, originalImage.PixelFormat);
            int strideProb = probData.Stride;
            int strideOrig = origData.Stride;
            unsafe
            {
                byte* probPtr = (byte*)probData.Scan0;
                byte* origPtr = (byte*)origData.Scan0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offsetProb = y * strideProb + x * 3;
                        int offsetOrig = y * strideOrig + x * 3;
                        // Since images are grayscale, read one channel.
                        byte pVal = probPtr[offsetProb];
                        prob[x, y] = pVal / 255f;
                        unary[x, y] = prob[x, y]; // use original probability as unary potential
                        byte iVal = origPtr[offsetOrig];
                        intensity[x, y] = iVal / 255f;
                    }
                }
            }
            probabilityMap.UnlockBits(probData);
            originalImage.UnlockBits(origData);

            // CRF smoothing: iterative update
            float[,] Q = (float[,])prob.Clone(); // initial Q = probability map
            float[,] newQ = new float[width, height];

            // Precompute denominator constant for bilateral weight:
            double twoSigmaSq = 2 * sigma * sigma;

            // Use 4-neighborhood: offsets (0,-1), (-1,0), (1,0), (0,1)
            int[] dx = { 0, -1, 1, 0 };
            int[] dy = { -1, 0, 0, 1 };

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        double weightedSum = 0;
                        double weightTotal = 0;
                        // For each neighbor:
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = x + dx[k];
                            int ny = y + dy[k];
                            if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                            {
                                double diff = intensity[x, y] - intensity[nx, ny];
                                double w = Math.Exp(-(diff * diff) / twoSigmaSq);
                                weightedSum += w * Q[nx, ny];
                                weightTotal += w;
                            }
                        }
                        // Update rule: combine unary potential and pairwise average
                        newQ[x, y] = (float)((unary[x, y] + lambda * weightedSum) / (1 + lambda * weightTotal));
                    }
                }
                // Swap newQ into Q for next iteration.
                Array.Copy(newQ, Q, width * height);
            }

            // Now convert final Q back to Bitmap.
            Bitmap refined = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData refinedData = refined.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, refined.PixelFormat);
            int strideRef = refinedData.Stride;
            unsafe
            {
                byte* refPtr = (byte*)refinedData.Scan0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte val = (byte)(Math.Max(0, Math.Min(1, Q[x, y])) * 255);
                        int offset = y * strideRef + x * 3;
                        refPtr[offset] = val;
                        refPtr[offset + 1] = val;
                        refPtr[offset + 2] = val;
                    }
                }
            }
            refined.UnlockBits(refinedData);
            return refined;
        }
    }
}
