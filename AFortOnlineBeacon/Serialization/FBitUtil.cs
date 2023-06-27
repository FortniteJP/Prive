﻿namespace AFortOnlineBeacon.Serialization;

public class FBitUtil {
    public static readonly byte[] GShift = {0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80};

    public static readonly byte[] GMask = {0x00, 0x01, 0x03, 0x07, 0x0f, 0x1f, 0x3f, 0x7f};
    
    public static unsafe void AppBitsCpy(byte* dest, int destBit, byte* src, int srcBit, int bitCount) {
        if (bitCount == 0) return;

        if (bitCount <= 8) {
            // Are these casts needed ?
            uint aDestIndex = (uint)(destBit / 8);
            uint aSrcIndex = (uint)(srcBit / 8);
            uint aLastDest = (uint)((destBit + bitCount - 1) / 8);
            uint aLastSrc = (uint)((srcBit + bitCount - 1) / 8);
            uint shiftSrc = (uint)(srcBit & 7);
            uint shiftDest = (uint)(destBit & 7);
            uint firstMask = (uint)(0xFF << (int)shiftDest);
            uint lastMask = (uint)(0xFE << ((destBit + bitCount - 1) & 7)); // Pre-shifted left by 1.	
            uint accu;

            if (aSrcIndex == aLastSrc) accu = (uint)(src[aSrcIndex] >> (int)shiftSrc);
            else accu = (uint)((src[aSrcIndex] >> (int)shiftSrc) | (src[aLastSrc] << (8 - (int)shiftSrc)));

            if (aDestIndex == aLastDest) {
                uint multiMask = firstMask & ~lastMask;
                dest[aDestIndex] = (byte) ((dest[aDestIndex] & ~multiMask) | ((accu << (int)shiftDest) & multiMask));
            } else {
                dest[aDestIndex] = (byte) ((dest[aDestIndex] & ~firstMask) | ((accu << (int)shiftDest) & firstMask));
                dest[aLastDest] = (byte) ((dest[aLastDest] & lastMask) | ((accu >> (8 - (int)shiftDest)) & ~lastMask));
            }

            return;
        }

        // Main copier, uses byte sized shifting. Minimum size is 9 bits, so at least 2 reads and 2 writes.
        uint destIndex = (uint)(destBit / 8);
        uint firstSrcMask = (uint)(0xFF << (destBit & 7));
        uint lastDest = (uint)((destBit + bitCount) / 8);
        uint lastSrcMask = (uint)(0xFF << ((destBit + bitCount) & 7));
        uint srcIndex = (uint)(srcBit / 8);
        uint lastSrc = (uint)((srcBit + bitCount) / 8);
        int shiftCount = (destBit & 7) - (srcBit & 7);
        int destLoop = (int)(lastDest - destIndex);
        int srcLoop = (int)(lastSrc - srcIndex);
        uint fullLoop;
        uint bitAccu;

        // Lead-in needs to read 1 or 2 source bytes depending on alignment.
        if (shiftCount >= 0) {
            fullLoop = (uint)Math.Max(destLoop, srcLoop);
            bitAccu = (uint)(src[srcIndex] << shiftCount);
            shiftCount += 8; //prepare for the inner loop.
        } else {
            shiftCount += 8; // turn shifts -7..-1 into +1..+7
            fullLoop = (uint)Math.Max(destLoop, srcLoop - 1);
            bitAccu = (uint)(src[srcIndex] << shiftCount);
            srcIndex++;
            shiftCount += 8; // Prepare for inner loop.  
            bitAccu = (uint)(((src[srcIndex] << shiftCount) + (bitAccu)) >> 8);
        }

        // Lead-in - first copy.
        dest[destIndex] = (byte)((bitAccu & firstSrcMask) | (dest[destIndex] & ~firstSrcMask));
        srcIndex++;
        destIndex++;

        // Fast inner loop. 
        for (; fullLoop > 1; fullLoop--) {   // ShiftCount ranges from 8 to 15 - all reads are relevant.
            bitAccu = (uint)(((src[srcIndex] << shiftCount) + (bitAccu)) >> 8); // Copy in the new, discard the old.
            srcIndex++;
            dest[destIndex] = (byte)bitAccu;  // Copy low 8 bits.
            destIndex++;
        }

        // Lead-out. 
        if (lastSrcMask != 0xFF) {
            if ((srcBit + bitCount - 1) / 8 == srcIndex) bitAccu = (uint)(((src[srcIndex] << shiftCount) + (bitAccu)) >> 8);
            else bitAccu = bitAccu >> 8;

            dest[destIndex] = (byte)((dest[destIndex] & lastSrcMask) | (bitAccu & ~lastSrcMask));
        }
    }
}