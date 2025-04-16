#region CLIP Tokenizer Implementation
using System.Collections.Generic;
using System.Linq;

using System.Text.RegularExpressions;

/// <summary>
/// Tokenizer configuration class that matches the JSON structure
/// </summary>
public class TokenizerConfig
{
    public bool AddPrefixSpace { get; set; }
    public string BosToken { get; set; } = "<|startoftext|>";
    public string EosToken { get; set; } = "<|endoftext|>";
    public string PadToken { get; set; } = "!";
    public int ModelMaxLength { get; set; } = 77;
    public bool DoLowerCase { get; set; } = true;
    public Dictionary<string, TokenConfig> AddedTokensDecoder { get; set; } = new Dictionary<string, TokenConfig>();

    public class TokenConfig
    {
        public string Content { get; set; }
        public bool Special { get; set; }
    }
}

/// <summary>
/// Implementation of the CLIP tokenizer
/// </summary>
public class CLIPTokenizer
{
    private readonly Dictionary<string, int> vocab;
    private readonly Dictionary<int, string> idToToken;
    private readonly Dictionary<string, bool> specialTokens;
    private readonly TokenizerConfig config;
    private readonly Regex patternSplit;
    private readonly Regex patternByteLevel;

    // Default token IDs
    private readonly int bosTokenId = 49406;
    private readonly int eosTokenId = 49407;
    private readonly int padTokenId = 0;

    /// <summary>
    /// Constructor for CLIPTokenizer
    /// </summary>
    /// <param name="vocab">The vocabulary dictionary</param>
    /// <param name="config">The tokenizer configuration (optional)</param>
    public CLIPTokenizer(Dictionary<string, int> vocab, TokenizerConfig config = null)
    {
        this.vocab = vocab;
        this.config = config ?? new TokenizerConfig();

        // Create reverse mapping
        this.idToToken = vocab.ToDictionary(kv => kv.Value, kv => kv.Key);

        // Set up special tokens
        this.specialTokens = new Dictionary<string, bool>();
        if (config != null && config.AddedTokensDecoder != null)
        {
            foreach (var token in config.AddedTokensDecoder)
            {
                if (token.Value.Special)
                {
                    specialTokens[token.Value.Content] = true;
                }
            }
        }

        // If special tokens not in config, set defaults
        if (!specialTokens.ContainsKey("<|startoftext|>"))
            specialTokens["<|startoftext|>"] = true;
        if (!specialTokens.ContainsKey("<|endoftext|>"))
            specialTokens["<|endoftext|>"] = true;

        // Compile regular expressions for tokenization
        // This pattern finds: words, numbers, and other special characters
        patternSplit = new Regex(@"'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.Compiled);

        // Pattern for byte-level encoding
        patternByteLevel = new Regex(@"[^\s\p{L}\p{N}]", RegexOptions.Compiled);
    }

    /// <summary>
    /// Encodes text to token IDs and attention mask
    /// </summary>
    /// <param name="text">The input text</param>
    /// <param name="maxLength">Maximum sequence length</param>
    /// <returns>Encoding with input IDs and attention mask</returns>
    public Encoding Encode(string text, int maxLength = 77)
    {
        // Normalize and preprocess text
        if (config.DoLowerCase)
            text = text.ToLower();

        if (config.AddPrefixSpace && !text.StartsWith(" "))
            text = " " + text;

        // Tokenize the text
        List<int> ids = new List<int>();

        // Add BOS token
        ids.Add(bosTokenId);

        // Split text into tokens
        var matches = patternSplit.Matches(text);
        foreach (Match match in matches)
        {
            string token = match.Value;

            // Process the token with ByteLevel encoding
            List<int> tokenIds = ByteLevelEncode(token);
            ids.AddRange(tokenIds);
        }

        // Add EOS token
        ids.Add(eosTokenId);

        // Truncate if needed
        if (ids.Count > maxLength)
        {
            ids = ids.Take(maxLength).ToList();

            // Ensure last token is EOS if we truncated
            if (ids.Count > 0 && maxLength > 0)
                ids[ids.Count - 1] = eosTokenId;
        }

        // Create attention mask (1 for all tokens)
        List<int> attentionMask = Enumerable.Repeat(1, ids.Count).ToList();

        // Pad sequences if needed
        while (ids.Count < maxLength)
        {
            ids.Add(padTokenId);
            attentionMask.Add(0);  // 0 for padding in attention mask
        }

        return new Encoding
        {
            InputIds = ids,
            AttentionMask = attentionMask
        };
    }

    /// <summary>
    /// Encodes a token at the byte level
    /// </summary>
    /// <param name="token">The token to encode</param>
    /// <returns>List of token IDs</returns>
    private List<int> ByteLevelEncode(string token)
    {
        var result = new List<int>();

        // Check if token is in vocabulary
        if (vocab.TryGetValue(token, out int id))
        {
            result.Add(id);
            return result;
        }

        // Byte-level encoding for unknown tokens
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(token);
        foreach (byte b in bytes)
        {
            // Convert each byte to its token representation
            // CLIP uses a specific format for bytes
            string byteToken = "Ġ" + b.ToString("X2").ToLower();

            if (vocab.TryGetValue(byteToken, out int byteId))
            {
                result.Add(byteId);
            }
            else if (vocab.TryGetValue("<|endoftext|>", out int unkId))
            {
                // Use EOS as unknown token if byte token not found
                result.Add(unkId);
            }
        }

        return result;
    }

    /// <summary>
    /// Result of the encoding process
    /// </summary>
    public class Encoding
    {
        public List<int> InputIds { get; set; } = new List<int>();
        public List<int> AttentionMask { get; set; } = new List<int>();
    }
}
#endregion