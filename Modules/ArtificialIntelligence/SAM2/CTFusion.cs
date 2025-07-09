//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
// CTFusion.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace CTS
{
    /// <summary>
    /// Provides various fusion strategies for segmentation volumes:
    ///   - Majority Voting,
    ///   - Weighted Averaging,
    ///   - Probability Map,
    ///   - Basic CRF smoothing.
    ///
    /// Typically, volumes are [width, height, depth], with each pixel a label or probability.
    /// </summary>
    public static class CTFusion
    {
        /// <summary>
        /// Fuse segmentation maps using majority voting.
        /// For each pixel, the label is the one that occurs most frequently.
        /// All inputs must be single-channel bitmaps with the same size.
        /// </summary>
        public static Bitmap MajorityVotingFusion(List<Bitmap> segmentationMaps)
        {
            if (segmentationMaps == null || segmentationMaps.Count == 0)
                throw new ArgumentException("No segmentation maps provided.");

            int width = segmentationMaps[0].Width;
            int height = segmentationMaps[0].Height;
            Bitmap fused = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            // Use naive approach: for each pixel, pick the label that appears the most
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var freq = new Dictionary<byte, int>();
                    foreach (var bmp in segmentationMaps)
                    {
                        Color c = bmp.GetPixel(x, y);
                        // We assume R=G=B for label
                        byte label = c.R;
                        if (!freq.ContainsKey(label)) freq[label] = 0;
                        freq[label]++;
                    }
                    byte finalLabel = freq.OrderByDescending(k => k.Value).First().Key;
                    fused.SetPixel(x, y, Color.FromArgb(finalLabel, finalLabel, finalLabel));
                }
            }
            return fused;
        }

        /// <summary>
        /// Fuse probability maps using Weighted Averaging. Each input is grayscale in [0..255].
        /// If weights are null, uses equal weighting.
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
                    byte fusedValue = (byte)(avgProb * 255.0);
                    fused.SetPixel(x, y, Color.FromArgb(fusedValue, fusedValue, fusedValue));
                }
            }
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
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int count = 0;
                    foreach (var bmp in segmentationMaps)
                    {
                        byte label = bmp.GetPixel(x, y).R;
                        if (label == targetLabel) count++;
                    }
                    double freq = (double)count / n;
                    byte val = (byte)(freq * 255);
                    probMap.SetPixel(x, y, Color.FromArgb(val, val, val));
                }
            }
            return probMap;
        }

        /// <summary>
        /// Simple CRF-like smoothing on a single grayscale mask, not a full multi-label CRF.
        ///
        /// </summary>
        public static Bitmap CRFSmoothing(Bitmap inputMask, int iterations = 1)
        {
            if (inputMask == null) return null;
            Bitmap result = new Bitmap(inputMask);
            for (int i = 0; i < iterations; i++)
            {
                result = SmoothOnce(result);
            }
            return result;
        }

        private static Bitmap SmoothOnce(Bitmap input)
        {
            int width = input.Width;
            int height = input.Height;
            Bitmap output = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // A trivial smoothing: average 3x3 neighborhood
                    int sum = 0;
                    int count = 0;
                    for (int yy = -1; yy <= 1; yy++)
                    {
                        for (int xx = -1; xx <= 1; xx++)
                        {
                            int ny = y + yy;
                            int nx = x + xx;
                            if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                            {
                                sum += input.GetPixel(nx, ny).R;
                                count++;
                            }
                        }
                    }
                    byte val = (byte)(sum / count);
                    output.SetPixel(x, y, Color.FromArgb(val, val, val));
                }
            }
            return output;
        }
    }
}