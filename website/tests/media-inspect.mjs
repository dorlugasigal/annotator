// Minimal readers for checking encoded media against its manifest. No dependencies.

function boxes(data, start = 0, end = data.length) {
  const found = [];
  for (let offset = start; offset + 8 <= end;) {
    let size = data.readUInt32BE(offset);
    const type = data.toString("latin1", offset + 4, offset + 8);
    let header = 8;
    if (size === 1) { size = Number(data.readBigUInt64BE(offset + 8)); header = 16; }
    else if (size === 0) size = end - offset;
    if (size < header || offset + size > end) throw new Error(`Malformed MP4 box ${type} at ${offset}.`);
    found.push({ type, start: offset, body: offset + header, end: offset + size });
    offset += size;
  }
  return found;
}
const child = (data, box, type) => boxes(data, box.body, box.end).find(item => item.type === type);

/** Reads the facts a web video manifest records: size, duration, tracks, codec and fast start. */
export function inspectMP4(data) {
  const top = boxes(data);
  const moov = top.find(box => box.type === "moov");
  const mdat = top.find(box => box.type === "mdat");
  if (!moov || !mdat) throw new Error("The MP4 needs moov and mdat boxes.");
  const mvhd = child(data, moov, "mvhd");
  const version = data[mvhd.body];
  const timescale = data.readUInt32BE(mvhd.body + (version === 1 ? 20 : 12));
  const length = version === 1 ? Number(data.readBigUInt64BE(mvhd.body + 24)) : data.readUInt32BE(mvhd.body + 16);
  const tracks = boxes(data, moov.body, moov.end).filter(box => box.type === "trak").map(trak => {
    const tkhd = child(data, trak, "tkhd");
    const mdia = child(data, trak, "mdia");
    const handler = data.toString("latin1", child(data, mdia, "hdlr").body + 8, child(data, mdia, "hdlr").body + 12);
    const stsd = child(data, child(data, child(data, mdia, "minf"), "stbl"), "stsd");
    return {
      handler,
      codec: data.toString("latin1", stsd.body + 12, stsd.body + 16),
      width: data.readUInt32BE(tkhd.end - 8) / 65536,
      height: data.readUInt32BE(tkhd.end - 4) / 65536,
    };
  });
  return { duration: length / timescale, tracks, fastStart: moov.start < mdat.start };
}

/** Width and height from a baseline or progressive JPEG frame header. */
export function jpegSize(data) {
  const {width, height} = jpegFrame(data);
  return {width, height};
}

/** The JPEG frame header: size, and whether the file is progressive (SOF2). */
export function jpegFrame(data) {
  if (data[0] !== 0xff || data[1] !== 0xd8) throw new Error("Not a JPEG file.");
  for (let offset = 2; offset + 9 < data.length;) {
    if (data[offset] !== 0xff) { offset++; continue; }
    const marker = data[offset + 1];
    if (marker >= 0xc0 && marker <= 0xcf && ![0xc4, 0xc8, 0xcc].includes(marker)) {
      return { width: data.readUInt16BE(offset + 7), height: data.readUInt16BE(offset + 5), progressive: marker === 0xc2 };
    }
    offset += 2 + data.readUInt16BE(offset + 2);
  }
  throw new Error("The JPEG has no frame header.");
}
