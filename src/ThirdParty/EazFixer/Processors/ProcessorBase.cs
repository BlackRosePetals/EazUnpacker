using System;

namespace EazFixer.Processors
{
    internal sealed class ProtectionNotFoundException : Exception
    {
        internal ProtectionNotFoundException(string message) : base(message) { }
    }

    internal abstract class ProcessorBase
    {
        public bool Initialized => _errorInitialized == null;
        public bool Processed => Initialized && _errorProcessed == null;
        public bool CleanedUp => Processed && _errorCleanup == null;
        public bool NotDetected { get; private set; }

        public string ErrorMessage => _errorInitialized ?? _errorProcessed ?? _errorCleanup;
        public int WarningCount { get; private set; }
        public string Summary { get; protected set; }
        public string DetailMessage => ErrorMessage ?? (Summary ?? "Processed; runtime types preserved.") +
            (WarningCount == 0 ? "" : " Warnings: " + WarningCount + ". " + string.Join(" | ", _warnings));

        protected EazContext Ctx;

        private string _errorInitialized;
        private string _errorProcessed;
        private string _errorCleanup;
        private readonly System.Collections.Generic.List<string> _warnings = new System.Collections.Generic.List<string>();

        protected void Warn(string message)
        {
            WarningCount++;
            if (_warnings.Count < 8) _warnings.Add(message);
        }

        public void Initialize(EazContext ctx)
        {
            Ctx = ctx;

            try {
                InitializeInternal();
            } catch (Exception e) {
                NotDetected = e is ProtectionNotFoundException;
                _errorInitialized = "Init error: " + e.Message;
            }
        }

        public void Process()
        {
            try {
                ProcessInternal();
            } catch (Exception e) {
                _errorProcessed = e.ToString();
            }
        }

        public void Cleanup()
        {
            try {
                if (!Flags.KeepTypes) CleanupInternal();
            } catch (Exception e) {
                _errorCleanup = "Cleanup error: " + e.Message;
            }
        }

        protected abstract void InitializeInternal();
        protected abstract void ProcessInternal();
        protected abstract void CleanupInternal();
    }
}
