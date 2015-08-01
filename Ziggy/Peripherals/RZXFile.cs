﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using zlib;

namespace Peripherals
{
    public class RZXFile
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct RZX_Header
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public char[] signature;

            public byte majorVersion;
            public byte minorVersion;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct RZX_Block
        {
            public byte id;
            public uint size;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct RZX_Creator
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public char[] author;

            public ushort majorVersion;
            public ushort minorVersion;
            //custom data of adjusted block size bytes follows
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct RZX_Snapshot
        {
            public uint flags;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public char[] extension;

            public uint uncompressedSize;
            //snapshot data/descriptor of adjusted block size bytes follows
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct RZX_SnapshotDescriptor 
        {
            public uint checksum;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct RZX_Record
        {
            public uint numFrames;
            public byte reserved;
            public uint tstatesAtStart;
            public uint flags;
            //sequence of frames follows
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct RZX_Frame
        {
            public ushort instructionCount;
            public ushort inputCount;
            public byte[] inputs;
        }

        public RZX_Header header;
        public RZX_Creator creator;
        public RZX_Record record;
        public RZX_Snapshot snap;
        public char[][] snapshotExtension = new char[2][];
        public byte[][] snapshotData = new byte[2][];

        private byte snapIndex = 0;
        private const string rzxSessionContinue = "Zero RZX Continue\0";
        private const string rzxSessionFinal = "Zero RZX Final   \0";

        //RZX Playback & Recording
        protected class RollbackBookmark {
            public SZXFile snapshot;
            public int frameIndex;
        };

        public int frameCount;
        public int fetchCount;
        public int inputCount;
        public RZX_Frame frame;
        protected int currentBookmark = 0;
        protected List<RollbackBookmark> bookmarks = new List<RollbackBookmark>();
        protected List<byte> inputs = new List<byte>();

        public List<RZX_Frame> frames = new List<RZX_Frame>();

        public bool LoadRZX(Stream fs) {
            using (BinaryReader r = new BinaryReader(fs, System.Text.Encoding.UTF8)) {
                int bytesToRead = (int)fs.Length;

                byte[] buffer = new byte[bytesToRead];
                int bytesRead = r.Read(buffer, 0, bytesToRead);

                if (bytesRead == 0)
                    return false; //something bad happened!

                GCHandle pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);

                //Read in the szx header to begin proceedings
                header = (RZX_Header)Marshal.PtrToStructure(pinnedBuffer.AddrOfPinnedObject(),
                                                                         typeof(RZX_Header));

                String sign = new String(header.signature);
                if (sign != "RZX!") {
                    pinnedBuffer.Free();
                    return false;
                }

                int bufferCounter = Marshal.SizeOf(header);

                while (bufferCounter < bytesRead) {
                    //Read the block info
                    RZX_Block block = (RZX_Block)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, bufferCounter),
                                                                         typeof(RZX_Block));

                    bufferCounter += Marshal.SizeOf(block);
                    switch (block.id) {
                        case 0x10:
                            creator = (RZX_Creator)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, bufferCounter),
                                                                         typeof(RZX_Creator));
                            break;

                        case 0x30:
                            snap = (RZX_Snapshot)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, bufferCounter),
                                                                         typeof(RZX_Snapshot));

                            int offset = bufferCounter + Marshal.SizeOf(snap);

                            if ((snap.flags & 0x2) != 0) {
                                int snapSize = (int)block.size - Marshal.SizeOf(snap) - Marshal.SizeOf(block);
                                MemoryStream compressedData = new MemoryStream(buffer, offset, snapSize);
                                MemoryStream uncompressedData = new MemoryStream();

                                using (ZInputStream zipStream = new ZInputStream(compressedData)) {
                                    byte[] tempBuffer = new byte[2048];
                                    int bytesUnzipped = 0;

                                    while ((bytesUnzipped = zipStream.read(tempBuffer, 0, 2048)) > 0) {
                                        uncompressedData.Write(tempBuffer, 0, bytesUnzipped);
                                    }
                                    snapshotData[snapIndex] = uncompressedData.ToArray();
                                    compressedData.Close();
                                    uncompressedData.Close();
                                }
                            }
                            else {
                                snapshotData[snapIndex] = new byte[snap.uncompressedSize];
                                Array.Copy(buffer, offset, snapshotData[snapIndex], 0, snap.uncompressedSize);
                            }

                            snapshotExtension[snapIndex] = snap.extension;
                            snapIndex += (byte)(snapIndex < 1 ? 1 : 0);
                            break;

                        case 0x80:
                            record = (RZX_Record)Marshal.PtrToStructure(Marshal.UnsafeAddrOfPinnedArrayElement(buffer, bufferCounter),
                                                                         typeof(RZX_Record));

                            int offset2 = bufferCounter + Marshal.SizeOf(record);
                            byte[] frameBuffer;
                            if ((record.flags & 0x2) != 0) {
                                int frameSize = (int)block.size - Marshal.SizeOf(record) - Marshal.SizeOf(block);
                                MemoryStream compressedData = new MemoryStream(buffer, offset2, frameSize);
                                MemoryStream uncompressedData = new MemoryStream();
                                using (ZInputStream zipStream = new ZInputStream(compressedData)) {
                                    byte[] tempBuffer = new byte[2048];
                                    int bytesUnzipped = 0;
                                    while ((bytesUnzipped = zipStream.read(tempBuffer, 0, 2048)) > 0) {
                                        uncompressedData.Write(tempBuffer, 0, bytesUnzipped);
                                    }
                                    frameBuffer = uncompressedData.ToArray();
                                    compressedData.Close();
                                    uncompressedData.Close();
                                }
                            } else //All frame data is supposed to be compressed, but just in case...
                            {
                                int frameSize = (int)block.size - Marshal.SizeOf(record) - Marshal.SizeOf(block);
                                frameBuffer = new byte[frameSize];
                                Array.Copy(buffer, offset2, frameBuffer, 0, frameSize);
                            }

                            offset2 = 0;
                            for (int f = 0; f < record.numFrames; f++) {
                                RZX_Frame frame = new RZX_Frame();
                                try {
                                    frame.instructionCount = BitConverter.ToUInt16(frameBuffer, offset2);
                                    offset2 += 2;
                                    frame.inputCount = BitConverter.ToUInt16(frameBuffer, offset2);
                                    offset2 += 2;
                                    if ((frame.inputCount == 65535)) {
                                        frame.inputCount = frames[frames.Count - 1].inputCount;
                                        frame.inputs = new byte[frame.inputCount];
                                        if (frame.inputCount > 0)
                                            Array.Copy(frames[frames.Count - 1].inputs, 0, frame.inputs, 0, frame.inputCount);
                                    } else if (frame.inputCount > 0) {
                                        frame.inputs = new byte[frame.inputCount];
                                        Array.Copy(frameBuffer, offset2, frame.inputs, 0, frame.inputCount);
                                        offset2 += frame.inputCount;
                                    } else {
                                        frame.inputs = new byte[0];
                                    }
                                    frames.Add(frame);
                                } catch (Exception e) {
                                    return false;
                                }
                            }

                            break;

                        default: //unrecognised block, so skip to next
                            break;
                    }
                    bufferCounter += (int)block.size - Marshal.SizeOf(block); //Move to next block
                }
                pinnedBuffer.Free();
            }
            return true;
        }

        public bool LoadRZX(string filename) {
            bool readSZX;
            using (FileStream fs = new FileStream(filename, FileMode.Open)) {
                readSZX = LoadRZX(fs);
            }
            return readSZX;
        }

        public static void CopyStream(System.IO.Stream input, System.IO.Stream output) {
            byte[] buffer = new byte[2000];
            int len;
            while ((len = input.Read(buffer, 0, 2000)) > 0) {
                output.Write(buffer, 0, len);
            }
            output.Flush();
        }

        public void SaveRZX(string filename) {
            header = new RZX_Header();
            header.majorVersion = 0;
            header.minorVersion = 12;
            header.flags = 0;
            header.signature = "RZX!".ToCharArray();

            creator = new RZX_Creator();
            creator.author = "Zero Emulator      \0".ToCharArray();
            creator.majorVersion = 6;
            creator.minorVersion = 0;

            using (FileStream fs = new FileStream(filename, FileMode.Create)) {
                using (BinaryWriter r = new BinaryWriter(fs)) {
                    byte[] buf;
                    buf = RawSerialize(header); //header is filled in by the callee machine
                    r.Write(buf);

                    RZX_Block block = new RZX_Block();
                    block.id = 0x10;
                    block.size = (uint)Marshal.SizeOf(creator) + 5;
                    buf = RawSerialize(block);
                    r.Write(buf);
                    buf = RawSerialize(creator);
                    r.Write(buf);

                    for (int f = 0; (f < snapshotData.Length) && (snapshotData[f] != null); f++) {
                        snap = new RZX_Snapshot();
                        snap.extension = "szx\0".ToCharArray();
                        snap.flags |= 0x2;
                        byte[] rawSZXData;
                        snap.uncompressedSize = (uint)snapshotData[f].Length;

                        using (MemoryStream outMemoryStream = new MemoryStream())
                        using (ZOutputStream outZStream = new ZOutputStream(outMemoryStream, zlibConst.Z_DEFAULT_COMPRESSION))
                        using (Stream inMemoryStream = new MemoryStream(snapshotData[f])) {
                            CopyStream(inMemoryStream, outZStream);
                            outZStream.finish();
                            rawSZXData = outMemoryStream.ToArray();
                        }

                        block.id = 0x30;
                        block.size = (uint)Marshal.SizeOf(snap) + (uint)rawSZXData.Length + 5;
                        buf = RawSerialize(block);
                        r.Write(buf);
                        buf = RawSerialize(snap);
                        r.Write(buf);
                        r.Write(rawSZXData);
                    }

                    record.numFrames = (uint)frames.Count;
                    block.id = 0x80;
                    byte[] rawFramesData;
                    using (MemoryStream outMemoryStream = new MemoryStream())
                    using (ZOutputStream outZStream = new ZOutputStream(outMemoryStream, zlibConst.Z_DEFAULT_COMPRESSION)) {
                        foreach (RZX_Frame frame in frames) {
                            using (Stream inMemoryStream = new MemoryStream()) {
                                BinaryWriter bw = new BinaryWriter(inMemoryStream);
                                bw.Write(frame.instructionCount);
                                bw.Write(frame.inputCount);
                                bw.Write(frame.inputs);
                                bw.Seek(0, 0);
                                CopyStream(inMemoryStream, outZStream);
                                bw.Close();
                            }
                        }

                        outZStream.finish();
                        rawFramesData = outMemoryStream.ToArray();
                    }

                    block.size = (uint)Marshal.SizeOf(record) + (uint)rawFramesData.Length + 5;
                    buf = RawSerialize(block);
                    r.Write(buf);
                    buf = RawSerialize(record);
                    r.Write(buf);
                    r.Write(rawFramesData);
                }
            }
        }

        private static byte[] RawSerialize(object anything) {
            int rawsize = Marshal.SizeOf(anything);
            IntPtr buffer = Marshal.AllocHGlobal(rawsize);
            Marshal.StructureToPtr(anything, buffer, false);
            byte[] rawdatas = new byte[rawsize];
            Marshal.Copy(buffer, rawdatas, 0, rawsize);
            Marshal.FreeHGlobal(buffer);
            return rawdatas;
        }

        public void InitPlayback() {
            frameCount = 0;
            fetchCount = 0;
            inputCount = 0;
            frame = frames[0];
        }

        public void ContinueRecording() {
            inputs = new List<byte>();
            frameCount = 0;
            fetchCount = 0;
            inputCount = 0;
        }

        public void InsertBookmark(SZXFile szx, List<byte> inputList ) {
            frame = new RZXFile.RZX_Frame();
            frame.inputCount = (ushort)inputList.Count;
            frame.instructionCount = (ushort)fetchCount;
            frame.inputs = inputList.ToArray();
            frames.Add(frame);
            fetchCount = 0;
            inputCount = 0;
         
            RollbackBookmark bookmark = new RollbackBookmark();
            bookmark.frameIndex = frames.Count;
            bookmark.snapshot = szx;
            bookmarks.Add(bookmark);
            currentBookmark = bookmarks.Count - 1;
        }

        public SZXFile Rollback() {
            if (bookmarks.Count > 0) {
                RollbackBookmark bookmark = bookmarks[currentBookmark];
                //if less than 2 seconds have passed since last bookmark, revert to an even earlier bookmark
                if ((frames.Count - bookmark.frameIndex) / 50 < 2) {
                    if (currentBookmark > 0) {
                        bookmarks.Remove(bookmark);
                        currentBookmark--;
                    }
                    bookmark = bookmarks[currentBookmark];
                }
                frames.RemoveRange(bookmark.frameIndex, frames.Count - bookmark.frameIndex);
                fetchCount = 0;
                inputCount = 0;
                inputs = new List<byte>();
                return bookmark.snapshot;
            }
            return null;
        }

        public void Discard() {
            bookmarks.Clear();
            inputs.Clear();
        }

        public void Save(string fileName, byte[] data) {
                snapshotData[1] = data;
                bookmarks.Clear();
                SaveRZX(fileName);
        }

        public void StartRecording(byte[] data, int totalTStates) {
            record.tstatesAtStart = (uint)totalTStates;
            record.flags |= 0x2; //Frames are compressed.
            snapshotData[0] = data;
        }

        public bool IsValidSession() {
            if (snapshotData[1] == null)
                return false;

            return true;
        }

        public bool NextPlaybackFrame() {
            frameCount++;
            fetchCount = 0;
            inputCount = 0;

            if (frameCount < frames.Count) {
                frame = frames[frameCount];
                return true;
            }

            return false;
        }

        public void RecordFrame(List<byte> inputList) {
            frame = new RZXFile.RZX_Frame();
            frame.inputCount = (ushort)inputList.Count;
            frame.instructionCount = (ushort)fetchCount;
            frame.inputs = inputList.ToArray();
            frames.Add(frame);
            fetchCount = 0;
            inputCount = 0;
        }

        public int GetPlaybackPercentage() {
            return frameCount * 100 / frames.Count;
        }
    }
}