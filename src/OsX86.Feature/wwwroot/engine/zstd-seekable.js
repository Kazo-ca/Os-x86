// Os-x86 engine: the zstd "seekable" format as images/tools/pack-image.py writes it (milestone 0,
// work item 6a; the format is contrib/seekable_format of zstd). An entry of the image payload is
// a run of independent zstd frames, each of a fixed decompressed size (blockSize, the last one
// shorter), followed by a skippable frame that holds the seek table:
//
//   [frame 0][frame 1]...[frame n-1][0x184D2A5E u32][size u32][(csize u32, dsize u32) x n][n u32][descriptor u8][0x8F92EAB1 u32]
//
// This file has no I/O: pure functions over bytes, used by disk.js in the engine worker and by
// the Node unit tests (tests/OsX86.Tests/Engine/disk-tests.js). Numbers are plain JavaScript
// numbers: an entry is at most 8 GiB (pack-image.py), far below 2^53.
(function (root) {
  "use strict";

  const SKIPPABLE_MAGIC = 0x184d2a5e;
  const SEEKABLE_MAGIC = 0x8f92eab1;
  const ZSTD_FRAME_MAGIC = 0xfd2fb528;
  const FOOTER_SIZE = 9; // Number_Of_Frames u32, Seek_Table_Descriptor u8, Seekable_Magic_Number u32
  const SKIPPABLE_HEADER_SIZE = 8;
  const MAX_FRAMES = 1 << 24;
  const MAX_FRAME_BYTES = 1 << 30; // pack-image.py refuses blocks above 1 GiB

  class SeekableFormatError extends Error {
    constructor(message) {
      super(message);
      this.name = "SeekableFormatError";
    }
  }

  function u32(bytes, at) {
    return (bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16)) + bytes[at + 3] * 0x1000000;
  }

  /**
   * Reads the 9-byte footer that ends an entry and says how many bytes the whole seek table
   * (skippable header included) occupies at the end of the entry.
   * @param {Uint8Array} footer the last FOOTER_SIZE bytes of the entry
   */
  function readFooter(footer) {
    if (!(footer instanceof Uint8Array) || footer.length < FOOTER_SIZE) {
      throw new SeekableFormatError("the seek table footer needs 9 bytes");
    }
    const at = footer.length - FOOTER_SIZE;
    const frameCount = u32(footer, at);
    const descriptor = footer[at + 4];
    const magic = u32(footer, at + 5);
    if (magic !== SEEKABLE_MAGIC) {
      throw new SeekableFormatError("the entry does not end with the zstd seekable magic number");
    }
    if (descriptor & 0x7c) {
      throw new SeekableFormatError("the seek table descriptor has reserved bits set");
    }
    if (frameCount === 0 || frameCount > MAX_FRAMES) {
      throw new SeekableFormatError(`the seek table claims ${frameCount} frames`);
    }
    const withChecksums = (descriptor & 0x80) !== 0;
    const entrySize = frameCount * (withChecksums ? 12 : 8);
    return { frameCount, withChecksums, tableSize: SKIPPABLE_HEADER_SIZE + entrySize + FOOTER_SIZE };
  }

  /**
   * Parses the whole seek table (the last `tableSize` bytes of the entry, as readFooter sized it)
   * into frames with their offsets in the compressed entry and in the decompressed content.
   * @param {Uint8Array} table
   * @param {number} entrySize the size of the whole entry, to place the frames
   */
  function parseSeekTable(table, entrySize) {
    const footer = readFooter(table);
    if (table.length !== footer.tableSize) {
      throw new SeekableFormatError(`the seek table is ${footer.tableSize} bytes, ${table.length} were given`);
    }
    if (!Number.isSafeInteger(entrySize) || entrySize < footer.tableSize) {
      throw new SeekableFormatError("the entry is smaller than its seek table");
    }
    if (u32(table, 0) !== SKIPPABLE_MAGIC || u32(table, 4) !== footer.tableSize - SKIPPABLE_HEADER_SIZE) {
      throw new SeekableFormatError("malformed seek table frame");
    }
    const stride = footer.withChecksums ? 12 : 8;
    const frames = new Array(footer.frameCount);
    let compressedOffset = 0;
    let decompressedOffset = 0;
    for (let index = 0; index < footer.frameCount; index++) {
      const at = SKIPPABLE_HEADER_SIZE + index * stride;
      const compressedSize = u32(table, at);
      const decompressedSize = u32(table, at + 4);
      if (compressedSize === 0 || decompressedSize === 0 || decompressedSize > MAX_FRAME_BYTES) {
        throw new SeekableFormatError(`frame ${index}: impossible sizes ${compressedSize} -> ${decompressedSize}`);
      }
      frames[index] = { compressedOffset, compressedSize, decompressedOffset, decompressedSize };
      compressedOffset += compressedSize;
      decompressedOffset += decompressedSize;
    }
    if (compressedOffset + footer.tableSize !== entrySize) {
      throw new SeekableFormatError(`the frames cover ${compressedOffset} bytes and the table ${footer.tableSize}; the entry has ${entrySize}`);
    }
    return { frames, rawSize: decompressedOffset, tableSize: footer.tableSize, withChecksums: footer.withChecksums };
  }

  /** Checks a parsed table against what the manifest says (rawSize, blockSize); throws when they disagree. */
  function checkAgainstManifest(table, rawSize, blockSize) {
    if (table.rawSize !== rawSize) {
      throw new SeekableFormatError(`the seek table decompresses to ${table.rawSize} bytes, the manifest says ${rawSize}`);
    }
    const frames = table.frames;
    for (let index = 0; index < frames.length; index++) {
      const size = frames[index].decompressedSize;
      if (size > blockSize || (index < frames.length - 1 && size !== blockSize)) {
        throw new SeekableFormatError(`frame ${index}: decompressed size ${size} does not match blockSize ${blockSize}`);
      }
    }
  }

  /** The index of the frame that holds decompressed byte `offset` (frames are sorted and contiguous). */
  function frameIndexAt(frames, offset) {
    let low = 0;
    let high = frames.length - 1;
    while (low < high) {
      const middle = (low + high + 1) >>> 1;
      if (frames[middle].decompressedOffset <= offset) {
        low = middle;
      } else {
        high = middle - 1;
      }
    }
    return low;
  }

  /**
   * The frames a read of `length` bytes at `offset` touches, with the slice of each one to copy.
   * Returns [{index, start, end, target}]: copy frame.bytes[start:end] to output[target:...].
   */
  function planRead(frames, rawSize, offset, length) {
    if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length) || offset < 0 || length < 0 || offset + length > rawSize) {
      throw new RangeError(`read of ${length} bytes at ${offset} is outside the ${rawSize} bytes of the entry`);
    }
    const plan = [];
    if (length === 0) {
      return plan;
    }
    let index = frameIndexAt(frames, offset);
    let position = offset;
    const end = offset + length;
    while (position < end) {
      const frame = frames[index];
      const start = position - frame.decompressedOffset;
      const stop = Math.min(frame.decompressedSize, end - frame.decompressedOffset);
      plan.push({ index, start, end: stop, target: position - offset });
      position += stop - start;
      index++;
    }
    return plan;
  }

  /**
   * The Frame_Content_Size of a zstd frame header, or null when the header does not carry one.
   * Throws when the bytes are not a zstd frame: the decoder of v86 traps on garbage instead of
   * reporting it, so the frame is checked here first.
   */
  function frameContentSize(frame) {
    if (!(frame instanceof Uint8Array) || frame.length < 6 || u32(frame, 0) !== ZSTD_FRAME_MAGIC) {
      throw new SeekableFormatError("not a zstd frame");
    }
    const descriptor = frame[4];
    const fcsFlag = descriptor >> 6;
    const singleSegment = (descriptor & 0x20) !== 0;
    if (descriptor & 0x08) {
      throw new SeekableFormatError("reserved bit set in the frame header");
    }
    const dictionaryIdSize = [0, 1, 2, 4][descriptor & 0x03];
    const fcsSize = fcsFlag === 0 ? (singleSegment ? 1 : 0) : [0, 2, 4, 8][fcsFlag];
    const at = 5 + (singleSegment ? 0 : 1) + dictionaryIdSize;
    if (fcsSize === 0) {
      return null;
    }
    if (frame.length < at + fcsSize) {
      throw new SeekableFormatError("truncated frame header");
    }
    switch (fcsSize) {
      case 1:
        return frame[at];
      case 2:
        return (frame[at] | (frame[at + 1] << 8)) + 256;
      case 4:
        return u32(frame, at);
      default: {
        const high = u32(frame, at + 4);
        if (high > 0x1fffff) {
          throw new SeekableFormatError("frame content size above 2^53");
        }
        return u32(frame, at) + high * 0x100000000;
      }
    }
  }

  const api = {
    SKIPPABLE_MAGIC,
    SEEKABLE_MAGIC,
    ZSTD_FRAME_MAGIC,
    FOOTER_SIZE,
    MAX_FRAMES,
    SeekableFormatError,
    readFooter,
    parseSeekTable,
    checkAgainstManifest,
    frameIndexAt,
    planRead,
    frameContentSize,
  };
  root.OsX86ZstdSeekable = api;
  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }
})(typeof self !== "undefined" ? self : globalThis);
