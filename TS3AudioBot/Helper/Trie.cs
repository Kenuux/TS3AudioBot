﻿using System;
using System.Text;

namespace TS3AudioBot.Helper
{
	public class Trie<T> where T : class
	{
		const int CHARS_IN_ALPHABET = 26;
		TrieNode root;

		public Trie()
		{
			root = new TrieNode(null, '+', null);
			root.unique = false;
		}

		public void Add(string key, T value)
		{
			TrieNode current = root;
			foreach (char c in key)
			{
				int index = ValidateChar(c);
				if (current.children == null)
				{
					current.children = new TrieNode[CHARS_IN_ALPHABET];
				}
				else
				{
					if (!current.hasData)
						current.data = null;
					current.unique = false;
				}
				TrieNode child = current.children[index];
				if (child == null)
				{
					child = new TrieNode(current, c, value);
					current.children[index] = child;
				}
				current = child;
			}
			current.data = value;
			current.hasData = true;
		}

		public bool TryGetValue(string key, out T value)
		{
			key = key.ToLower();
            TrieNode current = root;
			foreach (char c in key)
			{
				int index = ValidateChar(c);
				if (index == -1 || current.children == null || (current = current.children[index]) == null)
				{
					value = null;
					return false;
				}
				if (current.unique) break;
			}
			value = current.data;
			return current.hasData || current.unique;
		}

		private static int ValidateChar(char c)
		{
			if (c >= 'a' && c <= 'z')
				return c - 'a';
			return -1;
		}

		public override string ToString()
		{
			StringBuilder strb = new StringBuilder();
			ToStringGen(root, strb);
			return strb.ToString();
		}

		private void ToStringGen(TrieNode tn, StringBuilder strb)
		{
			strb.Append(tn.charId);
			if (tn.hasData)
			{
				strb.Append("[");
				strb.Append(tn.data);
				strb.Append("]");
			}
			else if (tn.unique)
			{
				strb.Append("*");
			}
			if (tn.children != null)
			{
				strb.Append("(");
				foreach (var tnc in tn.children)
				{
					if (tnc != null)
						ToStringGen(tnc, strb);
				}
				strb.Append(")");
			}
		}

		private class TrieNode
		{
			public T data;
			public TrieNode parent;
			public TrieNode[] children;
			public char charId;
			public bool hasData;
			public bool unique;

			public TrieNode(TrieNode trieNode, char charId, T data)
			{
				this.charId = charId;
				this.data = data;
				this.parent = trieNode;
				hasData = false;
				unique = true;
				children = null;
			}
		}
	}
}
