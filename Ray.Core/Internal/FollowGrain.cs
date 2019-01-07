﻿using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Ray.Core.Abstractions;
using Ray.Core.Exceptions;

namespace Ray.Core.Internal
{
    public abstract class FollowGrain<K, S, W> : Grain
        where S : class, IState<K>, new()
        where W : IBytesWrapper
    {
        public FollowGrain(ILogger logger)
        {
            Logger = logger;
            GrainType = GetType();
        }
        protected RayOptions ConfigOptions { get; private set; }
        protected ILogger Logger { get; private set; }
        protected IJsonSerializer JsonSerializer { get; private set; }
        protected ISerializer Serializer { get; private set; }
        protected IStorageContainer StorageContainer { get; private set; }
        /// <summary>
        /// Memory state, restored by snapshot + Event play or replay
        /// </summary>
        protected S State { get; set; }
        public abstract K GrainId { get; }
        /// <summary>
        /// 是否需要保存快照
        /// </summary>
        protected virtual bool SaveSnapshot => true;
        /// <summary>
        /// Grain保存快照的事件Version间隔
        /// </summary>
        protected virtual int SnapshotVersionInterval => ConfigOptions.FollowSnapshotVersionInterval;
        /// <summary>
        /// Grain失活的时候保存快照的最小事件Version间隔
        /// </summary>
        protected virtual int SnapshotMinVersionInterval => ConfigOptions.FollowSnapshotMinVersionInterval;
        /// <summary>
        /// 分批次批量读取事件的时候每次读取的数据量
        /// </summary>
        protected virtual int NumberOfEventsPerRead => ConfigOptions.NumberOfEventsPerRead;
        /// <summary>
        /// 事件处理的超时时间
        /// </summary>
        protected virtual int EventAsyncProcessTimeoutSeconds => ConfigOptions.EventAsyncProcessTimeoutSeconds;
        /// <summary>
        /// 是否全量激活，true代表启动时会执行大于快照版本的所有事件,false代表更快的启动，后续有事件进入的时候再处理大于快照版本的事件
        /// </summary>
        protected virtual bool FullyActive => false;
        /// <summary>
        /// 快照的事件版本号
        /// </summary>
        protected long SnapshotEventVersion { get; set; }
        /// <summary>
        /// 是否开启事件并发处理
        /// </summary>
        protected virtual bool EventConcurrentProcessing => false;
        protected Type GrainType { get; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask<IEventStorage<K>> GetEventStorage()
        {
            return StorageContainer.GetEventStorage<K, S>(this, GrainId);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask<IStateStorage<S, K>> GetStateStorage()
        {
            return StorageContainer.GetStateStorage<K, S>(this, GrainId);
        }
        #region 初始化数据
        /// <summary>
        /// 依赖注入统一方法
        /// </summary>
        protected virtual void DependencyInjection()
        {
            ConfigOptions = ServiceProvider.GetService<IOptions<RayOptions>>().Value;
            StorageContainer = ServiceProvider.GetService<IStorageContainer>();
            Serializer = ServiceProvider.GetService<ISerializer>();
            JsonSerializer = ServiceProvider.GetService<IJsonSerializer>();
        }
        public override async Task OnActivateAsync()
        {
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace(LogEventIds.GrainActivateId, "Start activation followgrain with id = {0}", GrainId.ToString());
            DependencyInjection();
            try
            {
                await ReadSnapshotAsync();
                if (FullyActive)
                {
                    await FullActive();
                }
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace(LogEventIds.GrainActivateId, "Followgrain activation completed with id = {0}", GrainId.ToString());
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Critical))
                    Logger.LogCritical(LogEventIds.FollowGrainActivateId, ex, "Followgrain activation failed with Id = {0}", GrainId.ToString());
                ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }
        private async Task FullActive()
        {
            var eventStorageTask = GetEventStorage();
            if (!eventStorageTask.IsCompleted)
                await eventStorageTask;
            while (true)
            {
                var eventList = await eventStorageTask.Result.GetListAsync(GrainId, State.Version, State.Version + NumberOfEventsPerRead);
                if (EventConcurrentProcessing)
                {
                    await Task.WhenAll(eventList.Select(@event =>
                    {
                        var task = OnEventDelivered(@event);
                        if (!task.IsCompleted)
                            return task.AsTask();
                        else
                            return Task.CompletedTask;
                    }));
                    var lastEvt = eventList.Last();
                    State.UnsafeUpdateVersion(lastEvt.Version, lastEvt.Timestamp);
                }
                else
                {
                    foreach (var @event in eventList)
                    {
                        State.IncrementDoingVersion(GrainType);//标记将要处理的Version
                        var task = OnEventDelivered(@event);
                        if (!task.IsCompleted)
                            await task;
                        State.UpdateVersion(@event, GrainType);//更新处理完成的Version
                    }
                }
                var saveTask = SaveSnapshotAsync();
                if (!saveTask.IsCompleted)
                    await saveTask;
                if (eventList.Count < NumberOfEventsPerRead) break;
            };
        }
        public override Task OnDeactivateAsync()
        {
            var needSaveSnap = State.Version - SnapshotEventVersion >= SnapshotMinVersionInterval;
            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInformation(LogEventIds.FollowGrainDeactivateId, "Followgrain start deactivation with id = {0} ,{1}", GrainId.ToString(), needSaveSnap ? "updated snapshot" : "no update snapshot");
            if (needSaveSnap)
                return SaveSnapshotAsync(true).AsTask();
            else
                return Task.CompletedTask;
        }
        /// <summary>
        /// true:当前状态无快照,false:当前状态已经存在快照
        /// </summary>
        protected bool NoSnapshot { get; private set; }
        protected virtual async Task ReadSnapshotAsync()
        {
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace(LogEventIds.GrainSnapshot, "Start read snapshot  with Id = {0} ,state version = {1}", GrainId.ToString(), State.Version);
            try
            {
                var stateStorageTask = GetStateStorage();
                if (!stateStorageTask.IsCompleted)
                    await stateStorageTask;
                State = await stateStorageTask.Result.GetByIdAsync(GrainId);
                if (State == null)
                {
                    NoSnapshot = true;
                    var createTask = CreateState();
                    if (!createTask.IsCompleted)
                        await createTask;
                }
                SnapshotEventVersion = State.Version;
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace(LogEventIds.GrainSnapshot, "The snapshot of id = {0} read completed, state version = {1}", GrainId.ToString(), State.Version);
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Critical))
                    Logger.LogCritical(LogEventIds.GrainSnapshot, ex, "The snapshot of id = {0} read failed", GrainId.ToString());
                ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }
        /// <summary>
        /// 初始化状态，必须实现
        /// </summary>
        /// <returns></returns>
        protected virtual ValueTask CreateState()
        {
            State = new S
            {
                StateId = GrainId
            };
            return new ValueTask();
        }
        #endregion
        public Task Tell(byte[] bytes)
        {
            using (var wms = new MemoryStream(bytes))
            {
                var message = Serializer.Deserialize<W>(wms);
                using (var ems = new MemoryStream(message.Bytes))
                {
                    if (Serializer.Deserialize(TypeContainer.GetType(message.TypeName), ems) is IEvent @event)
                    {
                        var tellTask = Tell(@event);
                        if (!tellTask.IsCompleted)
                            return tellTask.AsTask();
                    }
                    else
                    {
                        if (Logger.IsEnabled(LogLevel.Information))
                            Logger.LogInformation(LogEventIds.FollowEventProcessing, "Receive non-event messages, grain Id = {0} ,message type = {1}", GrainId.ToString(), message.TypeName);
                    }
                }
            }
            return Task.CompletedTask;
        }
        protected async ValueTask Tell(IEvent @event)
        {
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace(LogEventIds.FollowEventProcessing, "Start event handling, grain Id = {0} and state version = {1},event type = {2} ,event = {3}", GrainId.ToString(), State.Version, @event.GetType().FullName, JsonSerializer.Serialize(@event));
            try
            {
                if (@event.Version == State.Version + 1)
                {
                    var onEventDeliveredTask = OnEventDelivered(@event);
                    if (!onEventDeliveredTask.IsCompleted)
                        await onEventDeliveredTask;
                    State.FullUpdateVersion(@event, GrainType);//更新处理完成的Version
                }
                else if (@event.Version > State.Version)
                {
                    var eventStorageTask = GetEventStorage();
                    if (!eventStorageTask.IsCompleted)
                        await eventStorageTask;
                    var eventList = await eventStorageTask.Result.GetListAsync(GrainId, State.Version, @event.Version);
                    foreach (var item in eventList)
                    {
                        var onEventDeliveredTask = OnEventDelivered(item);
                        if (!onEventDeliveredTask.IsCompleted)
                            await onEventDeliveredTask;
                        State.FullUpdateVersion(item, GrainType);//更新处理完成的Version
                    }
                }
                if (@event.Version == State.Version + 1)
                {
                    var onEventDeliveredTask = OnEventDelivered(@event);
                    if (!onEventDeliveredTask.IsCompleted)
                        await onEventDeliveredTask;
                    State.FullUpdateVersion(@event, GrainType);//更新处理完成的Version
                }
                if (@event.Version > State.Version)
                {
                    throw new EventVersionNotMatchStateException(GrainId.ToString(), GrainType, @event.Version, State.Version);
                }
                await SaveSnapshotAsync();
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace(LogEventIds.FollowEventProcessing, "Event Handling Completion, grain Id ={0} and state version = {1},event type = {2}", GrainId.ToString(), State.Version, @event.GetType().FullName);
            }
            catch (Exception ex)
            {
                if (Logger.IsEnabled(LogLevel.Critical))
                    Logger.LogCritical(LogEventIds.FollowEventProcessing, ex, "FollowGrain Event handling failed with Id = {0},event = {1}", GrainId.ToString(), JsonSerializer.Serialize(@event));
                ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask OnEventDelivered(IEvent @event) => new ValueTask();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask OnSaveSnapshot() => new ValueTask();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual ValueTask OnSavedSnapshot() => new ValueTask();
        protected virtual async ValueTask SaveSnapshotAsync(bool force = false)
        {
            if (SaveSnapshot)
            {
                if (force || (State.Version - SnapshotEventVersion >= SnapshotVersionInterval))
                {
                    if (Logger.IsEnabled(LogLevel.Trace))
                        Logger.LogTrace(LogEventIds.FollowGrainSaveSnapshot, "Start saving state snapshots with Id = {0} ,state version = {1}", GrainId.ToString(), State.Version);
                    try
                    {
                        var onSaveSnapshotTask = OnSaveSnapshot();//自定义保存项
                        if (!onSaveSnapshotTask.IsCompleted)
                            await onSaveSnapshotTask;
                        var getStateStorageTask = GetStateStorage();
                        if (!getStateStorageTask.IsCompleted)
                            await getStateStorageTask;
                        if (NoSnapshot)
                        {
                            await getStateStorageTask.Result.InsertAsync(State);
                            NoSnapshot = false;
                        }
                        else
                        {
                            await getStateStorageTask.Result.UpdateAsync(State);
                        }
                        SnapshotEventVersion = State.Version;
                        var onSavedSnapshotTask = OnSavedSnapshot();
                        if (!onSavedSnapshotTask.IsCompleted)
                            await onSavedSnapshotTask;
                        if (Logger.IsEnabled(LogLevel.Trace))
                            Logger.LogTrace(LogEventIds.FollowGrainSaveSnapshot, "State snapshot saved successfully with Id {0} ,state version = {1}", GrainId.ToString(), State.Version);
                    }
                    catch (Exception ex)
                    {
                        if (Logger.IsEnabled(LogLevel.Error))
                            Logger.LogError(LogEventIds.FollowGrainSaveSnapshot, ex, "State snapshot save failed with Id = {0}", GrainId.ToString());
                        ExceptionDispatchInfo.Capture(ex).Throw();
                    }
                }
            }
        }
    }
}
