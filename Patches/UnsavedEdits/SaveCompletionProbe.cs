using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 原版事件/结局页的「保存」是 async void，拿不到 Task。编译器生成的
    /// AsyncVoidMethodBuilder 在方法开始时调用当前同步上下文的 OperationStarted，
    /// 结束（含异常）时调用 OperationCompleted，未处理异常经 Post 投递
    /// ExceptionDispatchInfo 后再抛出。保存调用期间临时换上这个转发上下文，
    /// 就能知道写盘何时真正结束、是否中途出错；所有回调仍原样转给游戏主线程上下文。
    /// </summary>
    internal sealed class SaveCompletionProbe : SynchronizationContext
    {
        private readonly SynchronizationContext _inner;
        private int _started;
        private int _completed;
        private int _raised;
        private Exception _failure;
        private Action _onFinished;
        private volatile bool _sealed;

        internal SaveCompletionProbe(SynchronizationContext inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        internal SynchronizationContext Inner => _inner;

        internal bool Started => Volatile.Read(ref _started) > 0;

        internal bool Finished
        {
            get
            {
                int started = Volatile.Read(ref _started);
                return started > 0 && Volatile.Read(ref _completed) >= started;
            }
        }

        internal Exception Failure => Volatile.Read(ref _failure);

        /// <summary>
        /// 保存调用返回后调用。之后所有已开始的异步操作都结束时，onFinished 会被投递到
        /// 游戏主线程执行一次。之前调用期间先开始又先结束的操作（例如提示框动画）
        /// 不会提前触发它。
        /// </summary>
        internal void Seal(Action onFinished)
        {
            _onFinished = onFinished;
            _sealed = true;
            RaiseIfFinished();
        }

        public override void OperationStarted()
        {
            Interlocked.Increment(ref _started);
            _inner.OperationStarted();
        }

        public override void OperationCompleted()
        {
            Interlocked.Increment(ref _completed);
            _inner.OperationCompleted();
            RaiseIfFinished();
        }

        public override void Post(SendOrPostCallback d, object state)
        {
            if (state is ExceptionDispatchInfo info)
                Interlocked.CompareExchange(ref _failure, info.SourceException, null);
            _inner.Post(d, state);
        }

        public override void Send(SendOrPostCallback d, object state) => _inner.Send(d, state);

        public override SynchronizationContext CreateCopy() => this;

        private void RaiseIfFinished()
        {
            if (!_sealed || !Finished) return;
            Action callback = _onFinished;
            if (callback == null || Interlocked.Exchange(ref _raised, 1) != 0) return;
            _inner.Post(_ => callback(), null);
        }
    }
}
