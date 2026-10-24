using System;
using System.Windows;
using System.Windows.Threading;

namespace LuaSTGMcpBridge.Threading
{
    /// <summary>
    /// Marshals work onto the WPF UI (dispatcher) thread.
    /// </summary>
    /// <remarks>
    /// The TCP bridge answers on thread-pool threads. <see cref="System.Windows.Application"/>,
    /// the window tree and every <c>DocumentData</c>/<c>TreeNode</c> that is bound to the
    /// <c>TreeView</c> belong to the UI thread, so any access to them from a socket callback
    /// must go through this class. Touching a bound WPF object off the UI thread throws
    /// <see cref="InvalidOperationException"/> (or silently corrupts binding state).
    /// </remarks>
    public static class UiThread
    {
        /// <summary>
        /// Gets the dispatcher of the running editor, or <c>null</c> when the bridge is used
        /// outside of a WPF application (unit tests, console harness).
        /// </summary>
        public static Dispatcher Dispatcher
        {
            get
            {
                Application application = Application.Current;
                return application == null ? null : application.Dispatcher;
            }
        }

        /// <summary>
        /// Gets whether a WPF application (and therefore a dispatcher) is available.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                Dispatcher dispatcher = Dispatcher;
                return dispatcher != null && !dispatcher.HasShutdownStarted;
            }
        }

        /// <summary>
        /// Gets whether the calling thread is the UI thread. When no dispatcher exists this
        /// returns <c>true</c> so callers fall back to running inline.
        /// </summary>
        public static bool IsOnUiThread
        {
            get
            {
                Dispatcher dispatcher = Dispatcher;
                return dispatcher == null || dispatcher.CheckAccess();
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the UI thread and waits for it to finish.
        /// Called from the UI thread it runs inline instead of dead-locking on itself.
        /// </summary>
        /// <param name="action">The work to run.</param>
        /// <exception cref="InvalidOperationException">No editor dispatcher is available.</exception>
        public static void Invoke(Action action)
        {
            Invoke<object>(() =>
            {
                action();
                return null;
            });
        }

        /// <summary>
        /// Runs <paramref name="function"/> on the UI thread, waits for it and returns its result.
        /// Called from the UI thread it runs inline.
        /// </summary>
        /// <typeparam name="T">Return type of the work.</typeparam>
        /// <param name="function">The work to run.</param>
        /// <returns>The value produced by <paramref name="function"/>.</returns>
        /// <exception cref="InvalidOperationException">No editor dispatcher is available.</exception>
        public static T Invoke<T>(Func<T> function)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));

            Dispatcher dispatcher = Dispatcher;

            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                throw new InvalidOperationException(
                    "The editor UI thread is not available: Application.Current is null or shutting down. "
                    + "Bridge requests that read or modify the node tree require a running editor.");
            }

            // Already on the UI thread: run inline. Dispatcher.Invoke would otherwise deadlock
            // or re-enter the dispatcher while it is already processing a message.
            if (dispatcher.CheckAccess()) return function();

            return dispatcher.Invoke(function);
        }

        /// <summary>
        /// Queues <paramref name="action"/> on the UI thread without waiting. Safe to call from
        /// the UI thread as well, in which case the work is queued after the current operation.
        /// </summary>
        /// <param name="action">The work to queue.</param>
        /// <returns><c>true</c> when the work was queued; <c>false</c> when no dispatcher is available.</returns>
        public static bool BeginInvoke(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            Dispatcher dispatcher = Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return false;

            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
            return true;
        }

        /// <summary>
        /// Tries to run <paramref name="action"/> on the UI thread, returning <c>false</c> instead
        /// of throwing when the editor is not available. Useful for fire-and-forget notifications.
        /// </summary>
        /// <param name="action">The work to run.</param>
        /// <returns><c>true</c> when the work ran.</returns>
        public static bool TryInvoke(Action action)
        {
            if (action == null) return false;
            if (!IsAvailable) return false;

            try
            {
                Invoke(action);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
