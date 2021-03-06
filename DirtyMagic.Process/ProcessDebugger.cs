using DirtyMagic.Breakpoints;
using DirtyMagic.Processes;
using DirtyMagic.WinAPI;
using DirtyMagic.WinAPI.Structures;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DirtyMagic.Exceptions;

namespace DirtyMagic
{
    public class ProcessDebugger : MemoryHandler
    {
        private Thread _debugThread = null;

        public bool IsDebugging { get; private set; }
        public bool IsDetached { get; private set; }
        public bool HasExited => !Process.IsValid;

        public List<HardwareBreakPoint> Breakpoints { get; private set; } = new List<HardwareBreakPoint>();

        public ProcessDebugger(int processId) : base(processId)
        {
        }

        public ProcessDebugger(RemoteProcess process) : base(process)
        {
        }

        private void Attach()
        {
            var res = false;
            if (!Kernel32.CheckRemoteDebuggerPresent(Process.Handle, ref res))
                throw new DebuggerException("Failed to check if remote process is already being debugged");

            if (res)
                throw new DebuggerException("Process is already being debugged by another debugger");

            if (!Kernel32.DebugActiveProcess(Process.Id))
                throw new DebuggerException("Failed to start debugging");

            if (!Kernel32.DebugSetProcessKillOnExit(false))
                throw new DebuggerException("Failed to set kill on exit");

            ClearUsedBreakpointSlots();

            IsDebugging = true;
        }

        public void ClearUsedBreakpointSlots()
        {
            RefreshMemory();
            foreach (var th in Process.Threads)
            {
                var hThread = Kernel32.OpenThread(ThreadAccess.THREAD_ALL_ACCESS, false, th.Id);
                if (hThread == IntPtr.Zero)
                    throw new BreakPointException("Can't open thread for access");

                HardwareBreakPoint.UnsetSlotsFromThread(hThread, SlotFlags.All);

                if (!Kernel32.CloseHandle(hThread))
                    throw new BreakPointException("Failed to close thread handle");
            }
        }

        public void AddBreakPoint(HardwareBreakPoint breakpoint)
        {
            if (Breakpoints.Count >= Kernel32.MaxHardwareBreakpointsCount)
                throw new DebuggerException("Can't set any more breakpoints");

            try
            {
                using (var suspender = MakeSuspender())
                {
                    breakpoint.Set(this);
                    Breakpoints.Add(breakpoint);
                }
            }
            catch (BreakPointException e)
            {
                throw new DebuggerException(e.Message);
            }
        }

        public void RemoveBreakPoints()
        {
            try
            {
                using (var suspender = MakeSuspender())
                {
                    foreach (var bp in Breakpoints)
                        bp.UnSet(this);
                }

                Breakpoints.Clear();
            }
            catch (BreakPointException e)
            {
                throw new DebuggerException(e.Message);
            }
        }

        public void StopDebugging() => IsDebugging = false;

        public void Join()
        {
            _debugThread?.Join();
        }

        protected void Detach()
        {
            if (IsDetached)
                return;
            IsDetached = true;

            RefreshMemory();
            if (HasExited)
                return;

            RemoveBreakPoints();

            if (!Kernel32.DebugActiveProcessStop(Process.Id))
                throw new DebuggerException("Failed to stop process debugging");
        }

        private void StartListener(uint waitInterval = 200)
        {
            var debugEvent = new DEBUG_EVENT();
            for (; IsDebugging;)
            {
                if (!Kernel32.WaitForDebugEvent(ref debugEvent, waitInterval))
                {
                    if (!IsDebugging)
                        break;
                    continue;
                }

                //Console.WriteLine("Debug Event Code: {0} ", DebugEvent.dwDebugEventCode);

                var okEvent = false;
                switch (debugEvent.dwDebugEventCode)
                {
                    case DebugEventType.RIP_EVENT:
                    case DebugEventType.EXIT_PROCESS_DEBUG_EVENT:
                    {
                        //Console.WriteLine("Process has exited");
                        IsDebugging = false;
                        IsDetached = true;

                        if (!Kernel32.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId,
                            okEvent
                                ? (uint) DebugContinueStatus.DBG_CONTINUE
                                : (uint) DebugContinueStatus.DBG_EXCEPTION_NOT_HANDLED))
                            throw new DebuggerException("Failed to continue debug event");
                        if (!Kernel32.DebugActiveProcessStop(Process.Id))
                            throw new DebuggerException("Failed to stop process debugging");
                        return;
                    }
                    case DebugEventType.EXCEPTION_DEBUG_EVENT:
                    {
                        //Console.WriteLine("Exception Code: {0:X}", DebugEvent.Exception.ExceptionRecord.ExceptionCode);
                        if (debugEvent.Exception.ExceptionRecord.ExceptionCode ==
                            (uint) ExceptonStatus.STATUS_SINGLE_STEP)
                        {
                            okEvent = true;

                            /*if (DebugEvent.dwThreadId != threadId)
                            {
                                Console.WriteLine("Debug event thread id does not match breakpoint thread");
                                break;
                            }*/

                            var hThread = Kernel32.OpenThread(ThreadAccess.THREAD_ALL_ACCESS, false,
                                debugEvent.dwThreadId);
                            if (hThread == IntPtr.Zero)
                                throw new DebuggerException("Failed to open thread");

                            var Context = new CONTEXT();
                            Context.ContextFlags = CONTEXT_FLAGS.CONTEXT_FULL;
                            if (!Kernel32.GetThreadContext(hThread, Context))
                                throw new DebuggerException("Failed to get thread context");

                            if (!Breakpoints.Any(e => e != null && e.IsSet && e.Address.ToUInt32() == Context.Eip))
                                break;
                            var bp = Breakpoints.First(e =>
                                e != null && e.IsSet && e.Address.ToUInt32() == Context.Eip);

                            var ContextWrapper = new ContextWrapper(this, Context);
                            if (bp.HandleException(ContextWrapper))
                            {
                                if (!Kernel32.SetThreadContext(hThread, ContextWrapper.Context))
                                    throw new DebuggerException("Failed to set thread context");
                            }
                        }

                        break;
                    }

                    case DebugEventType.CREATE_THREAD_DEBUG_EVENT:
                    {
                        foreach (var bp in Breakpoints)
                            bp.SetToThread(debugEvent.CreateThread.hThread, debugEvent.dwThreadId);
                        break;
                    }

                    case DebugEventType.EXIT_THREAD_DEBUG_EVENT:
                    {
                        foreach (var bp in Breakpoints)
                            bp.UnregisterThread(debugEvent.dwThreadId);
                        break;
                    }
                }

                if (!IsDebugging)
                {
                    IsDetached = true;

                    RemoveBreakPoints();
                    if (!Kernel32.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, okEvent ? (uint)DebugContinueStatus.DBG_CONTINUE : (uint)DebugContinueStatus.DBG_EXCEPTION_NOT_HANDLED))
                        throw new DebuggerException("Failed to continue debug event");
                    if (!Kernel32.DebugActiveProcessStop(Process.Id))
                        throw new DebuggerException("Failed to stop process debugging");
                    return;
                }

                if (!Kernel32.ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, okEvent ? (uint)DebugContinueStatus.DBG_CONTINUE : (uint)DebugContinueStatus.DBG_EXCEPTION_NOT_HANDLED))
                    throw new DebuggerException("Failed to continue debug event");
            }

            Detach();
        }

        public void Run()
        {
            _debugThread = new Thread(() =>
                {
                    try
                    {
                        Attach();
                        StartListener();
                    }
                    catch (DebuggerException e)
                    {
                        Console.WriteLine("Debugger exception occured: {0}", e.Message);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Exception occured: {0}", e.Message);
                    }
                    try
                    {
                        Detach();
                    }
                    catch (DebuggerException e)
                    {
                        Console.WriteLine("Debugger exception occured: {0}", e.Message);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Exception occured: {0}", e.Message);
                    }
                });
            _debugThread.Start();
        }

        public bool WaitForComeUp(int waitTime)
        {
            if (IsDebugging)
                return true;

            Thread.Sleep(waitTime);
            return IsDebugging;
        }

        public bool WaitForComeUp(int waitTime, int repeatCount)
        {
            for (int i = 0; i < repeatCount; ++i)
            {
                if (WaitForComeUp(waitTime))
                    return true;
            }

            return IsDebugging;
        }

        private bool _catchSigInt;
        public bool CatchSigInt
        {
            get => _catchSigInt;
            set
            {
                _catchSigInt = value;
                if (_catchSigInt)
                    SigIntHandler.AddInstance(this);
                else
                    SigIntHandler.RemoveInstance(this);
            }
        }
    }

    public static class SigIntHandler
    {
        private static readonly List<ProcessDebugger> AffectedInstances = new List<ProcessDebugger>();

        static SigIntHandler()
        {
            Console.CancelKeyPress += (sender, e) =>
            {
                if (AffectedInstances.Count > 0)
                {
                    e.Cancel = true;

                    foreach (var Debugger in AffectedInstances)
                        Debugger.StopDebugging();
                }
            };
        }

        public static void AddInstance(ProcessDebugger debugger) => AffectedInstances.Add(debugger);

        public static void RemoveInstance(ProcessDebugger debugger) => AffectedInstances.Remove(debugger);
    }
}
