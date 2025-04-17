#region CLIP Tokenizer Implementation
using System;
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
    public int ModelMaxLength { get; set; } = 16;
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

    // Token IDs based on config or defaults
    private readonly int bosTokenId;
    private readonly int eosTokenId;
    private readonly int padTokenId;

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

        // Set up token IDs from vocabulary if possible
        if (config != null)
        {
            // Get token IDs from vocab using the config's token strings
            if (!string.IsNullOrEmpty(config.BosToken) && vocab.TryGetValue(config.BosToken, out int bos))
                bosTokenId = bos;
            else
                bosTokenId = 49406; // Default CLIP BOS token ID

            if (!string.IsNullOrEmpty(config.EosToken) && vocab.TryGetValue(config.EosToken, out int eos))
                eosTokenId = eos;
            else
                eosTokenId = 49407; // Default CLIP EOS token ID

            if (!string.IsNullOrEmpty(config.PadToken) && vocab.TryGetValue(config.PadToken, out int pad))
                padTokenId = pad;
            else
                padTokenId = 0; // Default padding token ID
        }
        else
        {
            // Use defaults if no config
            bosTokenId = 49406;
            eosTokenId = 49407;
            padTokenId = 0;
        }

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
    /// Gets the model's maximum sequence length
    /// </summary>
    public int GetModelMaxLength()
    {
        return config?.ModelMaxLength ?? 16;
    }

    /// <summary>
    /// Encodes text to token IDs and attention mask
    /// </summary>
    /// <param name="text">The input text</param>
    /// <param name="maxLength">Maximum sequence length (if not specified, uses config)</param>
    /// <returns>Encoding with input IDs and attention mask</returns>
    public Encoding Encode(string text, int? maxLength = null)
    {
        // Use provided maxLength or fall back to config's ModelMaxLength
        int actualMaxLength = maxLength ?? config?.ModelMaxLength ?? 16;

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

            // Early stop if we're exceeding max length (account for EOS token)
            if (ids.Count >= actualMaxLength - 1)
                break;
        }

        // Add EOS token
        ids.Add(eosTokenId);

        // Truncate if needed
        if (ids.Count > actualMaxLength)
        {
            ids = ids.Take(actualMaxLength).ToList();

            // Ensure last token is EOS if we truncated
            if (ids.Count > 0 && actualMaxLength > 0)
                ids[ids.Count - 1] = eosTokenId;
        }

        // Create attention mask (1 for all tokens)
        List<int> attentionMask = Enumerable.Repeat(1, ids.Count).ToList();

        // Pad sequences if needed
        while (ids.Count < actualMaxLength)
        {
            ids.Add(padTokenId);
            attentionMask.Add(0);  // 0 for padding in attention mask
        }

        return new Encoding
        {
            InputIds = ids,
            AttentionMask = attentionMask.Select(i => (long)i).ToList()
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

        // Check if token starts with space
        bool startsWithSpace = token.StartsWith(" ");
        string tokenWithoutSpace = startsWithSpace ? token.Substring(1) : token;

        // Check if full token with space prefix is in vocabulary
        if (startsWithSpace && vocab.TryGetValue("Ġ" + tokenWithoutSpace, out id))
        {
            result.Add(id);
            return result;
        }

        // If token not in vocabulary, try splitting into characters
        foreach (char c in token)
        {
            string charToken = c.ToString();

            // CLIP tokenizer has special prefix "Ġ" for characters after space
            if (startsWithSpace && result.Count == 0)
            {
                charToken = "Ġ" + charToken;
                startsWithSpace = false;
            }

            if (vocab.TryGetValue(charToken, out id))
            {
                result.Add(id);
            }
            else
            {
                // Fallback to byte-level encoding for individual chars
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(charToken);
                foreach (byte b in bytes)
                {
                    // Format is different for first byte after space
                    string byteToken;
                    if (charToken.StartsWith("Ġ"))
                        byteToken = "Ġ" + b.ToString("X2").ToLower();
                    else
                        byteToken = "<|byte" + b.ToString() + "|>";

                    if (vocab.TryGetValue(byteToken, out int byteId))
                    {
                        result.Add(byteId);
                    }
                    else if (vocab.TryGetValue("<|endoftext|>", out int unkId))
                    {
                        // Use unknown token if byte token not found
                        result.Add(unkId);
                    }
                }
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
        public List<long> AttentionMask { get; set; } = new List<long>();
    }
}
#endregion