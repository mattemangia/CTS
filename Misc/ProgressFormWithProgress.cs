using System;

namespace CTSegmenter
{
    /// <summary>
    /// Extended version of ProgressForm that also implements IProgress<int>
    /// to maintain compatibility with methods that expect IProgress<int>
    /// </summary>
    public class ProgressFormWithProgress : ProgressForm, IProgress<int>
    {
        public ProgressFormWithProgress(string text = "Loading dataset...") : base(text)
        {
        }

        /// <summary>
        /// IProgress<int> implementation
        /// </summary>
        public void Report(int value)
        {
            UpdateProgress(value);
        }
    }
}