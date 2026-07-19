// Lit le token GitHub depuis le gestionnaire d'identifiants Windows et l'ecrit sur stdout.
// Sert aux publications de release via l'API REST (gh CLI renvoie 401 ici).
// Remplace l'ancien gh-release.exe qui vivait dans un scratchpad de session et a ete perdu au nettoyage.
using System;
using System.Runtime.InteropServices;

internal static class Program
{
    private const int CRED_TYPE_GENERIC = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr cred);

    private static string Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0) return null;
            var secret = Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
            return string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        }
        finally { CredFree(ptr); }
    }

    private static int Main(string[] args)
    {
        // Priorite a l'environnement, puis aux entrees ecrites par Git Credential Manager / gh CLI.
        var targets = new[]
        {
            "git:https://github.com",
            "git:https://github.com/",
            "LegacyGeneric:target=git:https://github.com",
            "gh:github.com",
        };

        var token = Environment.GetEnvironmentVariable("GH_TOKEN")
                    ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            foreach (var t in targets)
            {
                token = Read(t);
                if (!string.IsNullOrWhiteSpace(token)) break;
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("Aucun token GitHub trouve (env GH_TOKEN/GITHUB_TOKEN ni gestionnaire d'identifiants).");
            return 1;
        }

        Console.Out.Write(token);
        return 0;
    }
}
