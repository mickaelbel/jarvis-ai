using System.Runtime.InteropServices;

namespace JarvisAI.Desktop;

// Attache Jarvis à un Windows Job Object avec KILL_ON_JOB_CLOSE : si le
// processus crash (ou est tué), tous les enfants (serveurs Python voix,
// navigateurs Playwright, tracker de gestes…) sont terminés par le noyau.
// Plus jamais d'orphelins qui gardent la caméra/le micro.
public static class ProcessJobGuard
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;
    private static IntPtr _job;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>À appeler une fois au démarrage. Non fatal en cas d'échec.</summary>
    public static void Install()
    {
        try
        {
            _job = CreateJobObject(IntPtr.Zero, "JarvisAI.ProcessTree");
            if (_job == IntPtr.Zero) throw new InvalidOperationException("CreateJobObject a échoué");

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            // KILL_ON_JOB_CLOSE : nettoie les orphelins (voix, navigateurs) quand Jarvis
            // meurt. BREAKAWAY_OK : autorise un enfant à se détacher du job — nécessaire
            // pour que le processus relancé lors d'un « Redémarrer » ne soit PAS tué
            // quand l'instance courante se ferme (sinon le nouveau est massacré lui aussi).
            info.BasicLimitInformation.LimitFlags =
                JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;
            if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info,
                    System.Runtime.InteropServices.Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new InvalidOperationException("SetInformationJobObject a échoué");

            if (!AssignProcessToJobObject(_job, GetCurrentProcess()))
                throw new InvalidOperationException("AssignProcessToJobObject a échoué");

            App.Log("[JobGuard] Arborescence de processus attachée au job (kill-on-close actif)");
        }
        catch (Exception ex)
        {
            App.Log("[JobGuard] Non installé : " + ex.Message);
        }
    }
}