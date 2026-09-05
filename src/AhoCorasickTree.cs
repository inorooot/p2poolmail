using System;
using System.Collections.Generic;

namespace p2poolmail
{
    // cp-algorithms style Aho-Corasick with fixed ASCII alphabet (0..127),
    // ASCII case-insensitive matching.
    internal sealed class AhoCorasickTree
    {
        private const int ALPHABET = 128;

        private readonly List<Node> _nodes;
        private readonly List<int> _patternLengths;

        public AhoCorasickTree(string[] keywords)
        {
            ArgumentNullException.ThrowIfNull(keywords);
            if (keywords.Length == 0)
                throw new ArgumentException("Keyword list must not be empty.", nameof(keywords));

            for (var i = 0; i < keywords.Length; i++)
            {
                if (string.IsNullOrEmpty(keywords[i]))
                    throw new ArgumentException($"Keyword at index {i} must not be null or empty.", nameof(keywords));
            }

            _nodes = new List<Node> { new Node() };
            _patternLengths = new List<int>();

            for (int i = 0; i < keywords.Length; i++) AddPattern(keywords[i]);
            BuildLinks();
        }

        public bool Contains(ReadOnlySpan<char> text)
        {
            int v = 0;
            foreach (var ch in text)
            {
                int c = Normalize(ch);
                if (c == -1) { v = 0; continue; }
                v = _nodes[v].Next[c];
                if (_nodes[v].OutCount > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the pattern id of the match that ENDS earliest while scanning
        /// the text left to right (Aho-Corasick reports matches at their end
        /// position), or -1 if nothing matches. Matching is case-insensitive
        /// for ASCII letters, consistent with the log-prefix pre-filter.
        /// </summary>
        public int FirstMatch(ReadOnlySpan<char> text)
        {
            int v = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int c = Normalize(text[i]);
                if (c == -1) { v = 0; continue; }
                v = _nodes[v].Next[c];
                var outs = _nodes[v].Out;
                if (outs != null && outs.Count > 0) return outs[0];
            }
            return -1;
        }

        public IEnumerable<KeyValuePair<int, int>> Search(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            int v = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int c = Normalize(text[i]);
                if (c == -1) { v = 0; continue; }
                v = _nodes[v].Next[c];
                var outs = _nodes[v].Out;
                if (outs != null)
                {
                    foreach (var pid in outs)
                    {
                        var plen = _patternLengths[pid];
                        yield return new KeyValuePair<int, int>(pid, i - plen + 1);
                    }
                }
            }
        }

        private void AddPattern(string pattern)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            int v = 0;
            for (int i = 0; i < pattern.Length; i++)
            {
                int c = Normalize(pattern[i]);
                if (c == -1) throw new ArgumentException("Only ASCII characters are supported", nameof(pattern));
                if (_nodes[v].Next[c] == -1)
                {
                    _nodes[v].Next[c] = _nodes.Count;
                    _nodes.Add(new Node());
                }
                v = _nodes[v].Next[c];
            }
            _nodes[v].AddOutput(_patternLengths.Count);
            _patternLengths.Add(pattern.Length);
        }

        /// <summary>
        /// Maps a char to its normalized alphabet index (0..127): ASCII letters
        /// are lowercased so matching is case-insensitive; non-ASCII chars map to -1,
        /// which is handled by restarting at the root (no pattern contains them).
        /// </summary>
        private static int Normalize(char ch)
        {
            int c = (int)ch;
            if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
            return c < ALPHABET ? c : -1;
        }

        private void BuildLinks()
        {
            var q = new Queue<int>();
            _nodes[0].Link = 0;

            for (int c = 0; c < ALPHABET; c++)
            {
                if (_nodes[0].Next[c] == -1)
                {
                    _nodes[0].Next[c] = 0;
                }
                else
                {
                    int u = _nodes[0].Next[c];
                    _nodes[u].Link = 0;
                    q.Enqueue(u);
                }
            }

            while (q.Count > 0)
            {
                int v = q.Dequeue();
                for (int c = 0; c < ALPHABET; c++)
                {
                    int u = _nodes[v].Next[c];
                    if (u != -1)
                    {
                        _nodes[u].Link = _nodes[_nodes[v].Link].Next[c];
                        var outLink = _nodes[_nodes[u].Link].Out;
                        if (outLink != null)
                        {
                            var destOut = _nodes[u].Out;
                            if (destOut == null) _nodes[u].Out = new List<int>(outLink);
                            else destOut.AddRange(outLink);
                        }
                        q.Enqueue(u);
                    }
                    else
                    {
                        _nodes[v].Next[c] = _nodes[_nodes[v].Link].Next[c];
                    }
                }
            }
        }

        private class Node
        {
            public readonly int[] Next;
            public int Link;
            public List<int>? Out;

            public Node()
            {
                Next = new int[ALPHABET];
                for (int i = 0; i < ALPHABET; i++) Next[i] = -1;
                Link = -1;
                Out = null;
            }

            public int OutCount => Out?.Count ?? 0;

            public void AddOutput(int patternId)
            {
                if (Out == null) Out = new List<int>(1);
                Out.Add(patternId);
            }
        }
    }
}