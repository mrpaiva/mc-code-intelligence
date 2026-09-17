using System.IO.MemoryMappedFiles;

namespace DeclIndex;

/// <summary>
/// Varre o corpus mapeado em memória atrás do símbolo como palavra inteira (limite = não [A-Za-z0-9_] nem byte
/// ≥ 0x80, que é acento em UTF-8), em fatias paralelas alinhadas em quebra de linha. Um acerto por linha, com o
/// "id:linha:" lido do início dela; acerto dentro do próprio prefixo (símbolo numérico) é descartado.
/// </summary>
public static unsafe class CorpusScanner
{
	private const long ChunkBytes = 8 << 20;

	public static List<CorpusHit> Scan(MemoryMappedViewAccessor view, long length, byte[] needle)
	{
		byte* pointer = null;
		view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

		try
		{
			var data = (nint)(pointer + view.PointerOffset);
			var chunks = Chunks(data, length);
			var hits = new List<CorpusHit>[chunks.Count];

			Parallel.For(0, chunks.Count, index => hits[index] = ScanRange(data, chunks[index].From, chunks[index].To, needle));

			return hits.SelectMany(chunk => chunk).ToList();
		}
		finally
		{
			view.SafeMemoryMappedViewHandle.ReleasePointer();
		}
	}

	private static List<(long From, long To)> Chunks(nint data, long length)
	{
		var count = (int)Math.Clamp(length / ChunkBytes, 1, Environment.ProcessorCount);
		var chunks = new List<(long, long)>(count);
		var from = 0L;

		for (var index = 1; index <= count; index++)
		{
			var to = index == count ? length : NextLineStart(data, length, length * index / count);
			if (to > from) chunks.Add((from, to));
			from = to;
		}

		return chunks;
	}

	private static long NextLineStart(nint data, long length, long position)
	{
		var rest = new ReadOnlySpan<byte>((byte*)data + position, (int)Math.Min(length - position, int.MaxValue));
		var newline = rest.IndexOf((byte)'\n');

		return newline < 0 ? length : position + newline + 1;
	}

	private static List<CorpusHit> ScanRange(nint data, long from, long to, byte[] needle)
	{
		var hits = new List<CorpusHit>();
		var span = new ReadOnlySpan<byte>((byte*)data + from, checked((int)(to - from)));
		var position = 0;

		while (position < span.Length)
		{
			var offset = span[position..].IndexOf(needle);
			if (offset < 0) break;

			var start = position + offset;
			var stop = start + needle.Length;
			var before = start == 0 ? (byte)'\n' : span[start - 1];
			var after = stop == span.Length ? (byte)'\n' : span[stop];

			if (IsWord(before) || IsWord(after) || !TryReadPrefix(span, start, out var hit))
			{
				position = start + 1;
				continue;
			}

			hits.Add(hit);
			var newline = span[stop..].IndexOf((byte)'\n');
			position = newline < 0 ? span.Length : stop + newline + 1;
		}

		return hits;
	}

	/// <summary>Lê "id:linha:" do início da linha que contém o acerto; falha se o acerto está dentro do prefixo.</summary>
	private static bool TryReadPrefix(ReadOnlySpan<byte> span, int hitStart, out CorpusHit hit)
	{
		hit = default!;
		var lineStart = span[..hitStart].LastIndexOf((byte)'\n') + 1;
		var prefix = span[lineStart..hitStart];
		var first = prefix.IndexOf((byte)':');
		if (first < 0) return false;

		var second = prefix[(first + 1)..].IndexOf((byte)':');
		if (second < 0) return false;

		if (!TryParse(prefix[..first], out var id) || !TryParse(prefix.Slice(first + 1, second), out var line)) return false;

		hit = new CorpusHit(id, line);
		return true;
	}

	private static bool TryParse(ReadOnlySpan<byte> digits, out int value)
	{
		value = 0;
		if (digits.Length == 0) return false;

		foreach (var digit in digits)
		{
			if (digit < '0' || digit > '9') return false;

			value = value * 10 + (digit - '0');
		}

		return true;
	}

	private static bool IsWord(byte value)
		=> value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or (byte)'_' or >= 0x80;
}
