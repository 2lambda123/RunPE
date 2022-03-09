﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RunPE.Helpers;
using RunPE.Internals;

namespace RunPE.Patchers
{
    internal class ExtraAPIPatcher
    {
        private byte[] _originalGetModuleHandleBytes;
        private string _getModuleHandleFuncName;
        private IntPtr _newFuncAlloc;
        private int _newFuncBytesCount;

        public bool PatchAPIs(IntPtr baseAddress)
        {
            _getModuleHandleFuncName = Encoding.UTF8.Equals(Program.encoding) ? "GetModuleHandleW" : "GetModuleHandleA";

#if DEBUG
            Console.WriteLine(
                $"\n[*] Patching kernelbase!{_getModuleHandleFuncName} to return base address of loaded PE if called with NULL");
#endif
            WriteNewFuncToMemory(baseAddress);

            if (PatchAPIToJmpToNewFunc()) return true;
#if DEBUG
            Console.WriteLine($"[-] Unable to patch kernelbase!{_getModuleHandleFuncName}");
#endif
            return false;
        }

        private bool PatchAPIToJmpToNewFunc()
        {
            // Patch the API to jump to out new func code
            var pointerBytes = BitConverter.GetBytes(_newFuncAlloc.ToInt64());

            /*
                0:  48 b8 88 77 66 55 44    movabs rax,<address of newFunc>
                7:  33 22 11
                a:  ff e0                   jmp    rax
             */
            var patchBytes = new List<byte>() {0x48, 0xB8};
            patchBytes.AddRange(pointerBytes);

            patchBytes.Add(0xFF);
            patchBytes.Add(0xE0);

            _originalGetModuleHandleBytes =
                Utils.PatchFunction("kernelbase", _getModuleHandleFuncName, patchBytes.ToArray());

            return _originalGetModuleHandleBytes != null;
        }

        private IntPtr WriteNewFuncToMemory(IntPtr baseAddress)
        {
            // Write some code to memory that will return our base address if arg0 is null or revert back to GetModuleAddress if not.
            var newFuncBytes = new List<byte>() {0x48, 0x85, 0xc9, 0x75, 0x0b};

            var moduleHandle = NativeDeclarations.GetModuleHandle("kernelbase");
            var getModuleHandleFuncAddress = NativeDeclarations.GetProcAddress(moduleHandle, _getModuleHandleFuncName);

            newFuncBytes.Add(0x48);
            newFuncBytes.Add(0xB8);

            var baseAddressPointerBytes = BitConverter.GetBytes(baseAddress.ToInt64());

            newFuncBytes.AddRange(baseAddressPointerBytes);

            newFuncBytes.Add(0xC3);
            newFuncBytes.Add(0x48);
            newFuncBytes.Add(0xB8);

            var patchLength = CalculatePatchLength(getModuleHandleFuncAddress);
            var pointerBytes = BitConverter.GetBytes(getModuleHandleFuncAddress.ToInt64() + patchLength);

            newFuncBytes.AddRange(pointerBytes);

            var originalInstructions = new byte[patchLength];
            Marshal.Copy(getModuleHandleFuncAddress, originalInstructions, 0, patchLength);
            newFuncBytes.AddRange(originalInstructions);

            newFuncBytes.Add(0xFF);
            newFuncBytes.Add(0xE0);
            /*
            0:  48 85 c9                test   rcx,rcx
            3:  75 0b                   jne    +0x0b
            5:  48 b8 88 77 66 55 44    movabs rax,<Base Address of mapped PE>
            c:  33 22 11
            f:  c3                      ret
            10:  48 b8 88 77 66 55 44   movabs rax,<Back to GetModuleHandle>
            17:  33 22 11
            ... original replaced opcodes...
            1a:  ff e0                  jmp    rax
            */
            _newFuncAlloc = NativeDeclarations.VirtualAlloc(IntPtr.Zero, (uint) newFuncBytes.Count,
                NativeDeclarations.MEM_COMMIT, NativeDeclarations.PAGE_READWRITE);
#if DEBUG
            Console.WriteLine($"[*] New func at: 0x{_newFuncAlloc.ToInt64():X}");
#endif
            Marshal.Copy(newFuncBytes.ToArray(), 0, _newFuncAlloc, newFuncBytes.Count);
            _newFuncBytesCount = newFuncBytes.Count;

            NativeDeclarations.VirtualProtect(_newFuncAlloc, (UIntPtr) newFuncBytes.Count,
                NativeDeclarations.PAGE_EXECUTE_READ, out _);
            return _newFuncAlloc;
        }

        private int CalculatePatchLength(IntPtr funcAddress)
        {
#if DEBUG
            Console.WriteLine($"[*] Calculating patch length for kernelbase!{_getModuleHandleFuncName}");
#endif
            var bytes = Utils.ReadMemory(funcAddress, 40);
            byte[] needle;
            if (_getModuleHandleFuncName == "GetModuleHandleA")
            {
                needle = new byte[] { 0x48, 0x4d, 0x4c };
            }
            else
            {
                needle = new byte[] { 0x48, 0xFF, 0x15 };
            }
            var searcher = new BoyerMoore(needle);
            var length = searcher.Search(bytes).FirstOrDefault() + 1;
            if (length == 1)
            {
                throw new Exception("Unable to calculate patch length, the function may have changed to a point it is is no longer recognised and this code needs to be updated");
            }
#if DEBUG
            Console.WriteLine($"[*] Patch length calculated to be: {length}");
#endif
            return length;
        }

        public bool RevertAPIs()
        {
#if DEBUG
            Console.WriteLine($"[*] Reverting patch to kernelbase!{_getModuleHandleFuncName}");
#endif
            Utils.PatchFunction("kernelbase", _getModuleHandleFuncName, _originalGetModuleHandleBytes);
            Utils.ZeroOutMemory(_newFuncAlloc, _newFuncBytesCount);
            Utils.FreeMemory(_newFuncAlloc);
#if DEBUG
            Console.WriteLine("[+] Extra API patches reverted\n");
#endif
            return true;
        }
    }
    
    public sealed class BoyerMoore
{
    readonly byte[] needle;
    readonly int[] charTable;
    readonly int[] offsetTable;

    public BoyerMoore(byte[] needle)
    {
        this.needle = needle;
        this.charTable = makeByteTable(needle);
        this.offsetTable = makeOffsetTable(needle);
    }

    public IEnumerable<int> Search(byte[] haystack)
    {
        if (needle.Length == 0)
            yield break;

        for (int i = needle.Length - 1; i < haystack.Length;)
        {
            int j;

            for (j = needle.Length - 1; needle[j] == haystack[i]; --i, --j)
            {
                if (j != 0)
                    continue;

                yield return i;
                i += needle.Length - 1;
                break;
            }

            i += Math.Max(offsetTable[needle.Length - 1 - j], charTable[haystack[i]]);
        }
    }

    static int[] makeByteTable(byte[] needle)
    {
        const int ALPHABET_SIZE = 256;
        int[] table = new int[ALPHABET_SIZE];

        for (int i = 0; i < table.Length; ++i)
            table[i] = needle.Length;

        for (int i = 0; i < needle.Length - 1; ++i)
            table[needle[i]] = needle.Length - 1 - i;

        return table;
    }

    static int[] makeOffsetTable(byte[] needle)
    {
        int[] table = new int[needle.Length];
        int lastPrefixPosition = needle.Length;

        for (int i = needle.Length - 1; i >= 0; --i)
        {
            if (isPrefix(needle, i + 1))
                lastPrefixPosition = i + 1;

            table[needle.Length - 1 - i] = lastPrefixPosition - i + needle.Length - 1;
        }

        for (int i = 0; i < needle.Length - 1; ++i)
        {
            int slen = suffixLength(needle, i);
            table[slen] = needle.Length - 1 - i + slen;
        }

        return table;
    }

    static bool isPrefix(byte[] needle, int p)
    {
        for (int i = p, j = 0; i < needle.Length; ++i, ++j)
            if (needle[i] != needle[j])
                return false;

        return true;
    }

    static int suffixLength(byte[] needle, int p)
    {
        int len = 0;

        for (int i = p, j = needle.Length - 1; i >= 0 && needle[i] == needle[j]; --i, --j)
            ++len;

        return len;
    }
}
}