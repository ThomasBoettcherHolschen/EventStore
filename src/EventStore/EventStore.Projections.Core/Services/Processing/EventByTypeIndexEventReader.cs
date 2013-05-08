// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Projections.Core.Messages;

namespace EventStore.Projections.Core.Services.Processing
{
    public class EventByTypeIndexEventReader : EventReader
    {
        internal const int _maxReadCount = 111;
        private readonly HashSet<string> _eventTypes;
        public readonly bool _resolveLinkTos;
        private readonly ITimeProvider _timeProvider;

        public class PendingEvent
        {
            public readonly EventRecord Event;
            public readonly EventRecord PositionEvent;
            public readonly float Progress;
            public readonly TFPos TfPosition;

            public PendingEvent(EventRecord @event, EventRecord positionEvent, TFPos tfPosition, float progress)
            {
                Event = @event;
                PositionEvent = positionEvent;
                Progress = progress;
                TfPosition = tfPosition;
            }
        }

        public int _deliveredEvents;
        public bool _readAllMode = false;
        public TFPos _fromTfPosition;
        public TFPos _lastEventPosition;
        private readonly IndexBased _indexBased;
        private readonly TfBased _tfBased;

        public EventByTypeIndexEventReader(
            IPublisher publisher, Guid eventReaderCorrelationId, string[] eventTypes, TFPos fromTfPosition,
            Dictionary<string, int> fromPositions, bool resolveLinkTos, ITimeProvider timeProvider,
            bool stopOnEof = false, int? stopAfterNEvents = null)
            : base(publisher, eventReaderCorrelationId, stopOnEof, stopAfterNEvents)
        {
            if (eventTypes == null) throw new ArgumentNullException("eventTypes");
            if (timeProvider == null) throw new ArgumentNullException("timeProvider");
            if (eventTypes.Length == 0) throw new ArgumentException("empty", "eventTypes");

            _timeProvider = timeProvider;
            _eventTypes = new HashSet<string>(eventTypes);
            _fromTfPosition = fromTfPosition;
            _lastEventPosition = new TFPos(0, -10);
            _resolveLinkTos = resolveLinkTos;

            _indexBased = new IndexBased(_eventTypes, this, fromPositions);
            _tfBased = new TfBased(timeProvider, this);
        }

        public override void Dispose()
        {
            _indexBased.Dispose();
            _tfBased.Dispose();
            base.Dispose();
        }

        protected override string FromAsText()
        {
            return _indexBased.FromAsText();
        }

        protected override void RequestEvents()
        {
            RequestEventsAll();
        }

        protected override bool AreEventsRequested()
        {
            return _indexBased.AreEventsRequested() || _tfBased._tfEventsRequested;
        }

        public void Handle(ClientMessage.ReadStreamEventsForwardCompleted message)
        {
            _indexBased.Handle(message);
        }

        private void CheckIdle()
        {
            if (_indexBased._eofs.All(v => v.Value))
                _publisher.Publish(
                    new ReaderSubscriptionMessage.EventReaderIdle(EventReaderCorrelationId, _timeProvider.Now));
        }

        public bool CheckEnough()
        {
            if (_stopAfterNEvents != null && _deliveredEvents >= _stopAfterNEvents)
            {
                _publisher.Publish(
                    new ReaderSubscriptionMessage.EventReaderEof(EventReaderCorrelationId, maxEventsReached: true));
                Dispose();
                return true;
            }
            return false;
        }

        public void RequestEventsAll()
        {
            if (_pauseRequested || _paused)
                return;
            if (_readAllMode)
            {
                _tfBased.RequestTfEvents(delay: false);
            }
            else
            {
                foreach (var stream in _indexBased._streamToEventType.Keys)
                    _indexBased.RequestEvents(stream, delay: false);
                _indexBased.RequestCheckpointStream(delay: false);
            }
        }

        public void PublishIORequest(bool delay, Message readEventsForward)
        {
            if (delay)
                _publisher.Publish(
                    TimerMessage.Schedule.Create(
                        TimeSpan.FromMilliseconds(250), new PublishEnvelope(_publisher, crossThread: true),
                        readEventsForward));
            else
                _publisher.Publish(readEventsForward);
        }

        public void Handle(ClientMessage.ReadStreamEventsBackwardCompleted message)
        {
            _indexBased.Handle(message);
        }

        public void Handle(ClientMessage.ReadAllEventsForwardCompleted message)
        {
            _tfBased.Handle(message);
        }

        public void UpdateNextStreamPosition(string eventStreamId, int nextPosition)
        {
            int streamPosition;
            if (!_indexBased._fromPositions.TryGetValue(eventStreamId, out streamPosition))
                streamPosition = -1;
            if (nextPosition > streamPosition)
                _indexBased._fromPositions[eventStreamId] = nextPosition;
        }

        public class IndexBased : IHandle<ClientMessage.ReadStreamEventsForwardCompleted>,
                                  IHandle<ClientMessage.ReadStreamEventsBackwardCompleted>

        {
            private const int _maxReadCount = 111;
            private readonly EventByTypeIndexEventReader _eventByTypeIndexEventReader;
            private readonly HashSet<string> _eventTypes;
            public readonly Dictionary<string, string> _streamToEventType;
            public readonly HashSet<string> _eventsRequested = new HashSet<string>();
            public bool _indexCheckpointStreamRequested = false;
            private int _lastKnownIndexCheckpointEventNumber = -1;
            private TFPos _lastKnownIndexCheckpointPosition = default(TFPos);

            private readonly Dictionary<string, Queue<PendingEvent>> _buffers =
                new Dictionary<string, Queue<PendingEvent>>();

            public readonly Dictionary<string, bool> _eofs;
            public readonly Dictionary<string, int> _fromPositions;
            private bool _disposed;

            public IndexBased(
                HashSet<string> eventTypes, EventByTypeIndexEventReader eventByTypeIndexEventReader,
                Dictionary<string, int> fromPositions)
            {
                _eventTypes = eventTypes;
                _streamToEventType = eventTypes.ToDictionary(v => "$et-" + v, v => v);
                _eofs = _streamToEventType.Keys.ToDictionary(v => v, v => false);
                _eventByTypeIndexEventReader = eventByTypeIndexEventReader;
                // whatever the first event returned is (even if we start from the same position as the last processed event
                // let subscription handle this 
                ValidateTag(fromPositions);
                _fromPositions = fromPositions;
            }

            private void ValidateTag(Dictionary<string, int> fromPositions)
            {
                if (_eventTypes.Count != fromPositions.Count)
                    throw new ArgumentException("Number of streams does not match", "fromPositions");

                foreach (var stream in _streamToEventType.Keys.Where(stream => !fromPositions.ContainsKey(stream)))
                {
                    throw new ArgumentException(
                        string.Format("The '{0}' stream position has not been set", stream), "fromPositions");
                }
            }


            public TFPos GetTargetEventPosition(PendingEvent head)
            {
                return head.TfPosition;
            }

            public string FromAsText()
            {
                return _fromPositions.ToString();
            }

            public void Handle(ClientMessage.ReadStreamEventsForwardCompleted message)
            {
                if (_eventByTypeIndexEventReader._disposed || _eventByTypeIndexEventReader._readAllMode)
                    return;
                if (message.EventStreamId == "$et")
                {
                    ReadIndexCheckpointStreamCompleted(message.Result, message.Events);
                    return;
                }

                if (!_streamToEventType.ContainsKey(message.EventStreamId))
                    throw new InvalidOperationException(
                        string.Format("Invalid stream name: {0}", message.EventStreamId));
                if (!_eventsRequested.Contains(message.EventStreamId))
                    throw new InvalidOperationException("Read events has not been requested");
                if (_eventByTypeIndexEventReader._paused)
                    throw new InvalidOperationException("Paused");
                _eventsRequested.Remove(message.EventStreamId);
                switch (message.Result)
                {
                    case ReadStreamResult.NoStream:
                        _eofs[message.EventStreamId] = true;
                        ProcessBuffers();
                        PauseOrContinueReadingStream(message.EventStreamId, delay: true);
                        CheckSwitch();
                        break;
                    case ReadStreamResult.Success:
                        _eventByTypeIndexEventReader.UpdateNextStreamPosition(
                            message.EventStreamId, message.NextEventNumber);
                        var isEof = message.Events.Length == 0;
                        if (isEof)
                        {
                            // the end
                            _eofs[message.EventStreamId] = true;
                        }
                        else
                        {
                            _eofs[message.EventStreamId] = false;
                            EnqueueEvents(message);
                        }
                        ProcessBuffers();
                        //TODO: IT MUST REQUEST EVENTS FROM NEW POSITION (CURRENTLY OLD)
                        PauseOrContinueReadingStream(message.EventStreamId, delay: isEof);
                        CheckSwitch();
                        break;
                    default:
                        throw new NotSupportedException(
                            string.Format("ReadEvents result code was not recognized. Code: {0}", message.Result));
                }
            }

            public void Handle(ClientMessage.ReadStreamEventsBackwardCompleted message)
            {
                if (_eventByTypeIndexEventReader._disposed || _eventByTypeIndexEventReader._readAllMode)
                    return;
                ReadIndexCheckpointStreamCompleted(message.Result, message.Events);
            }

            private void EnqueueEvents(ClientMessage.ReadStreamEventsForwardCompleted message)
            {
                for (int index = 0; index < message.Events.Length; index++)
                {
                    var @event = message.Events[index].Event;
                    var @link = message.Events[index].Link;
                    EventRecord positionEvent = (link ?? @event);
                    var queue = GetStreamQueue(positionEvent);
                    //TODO: progress calculation below is incorrect.  sum(current)/sum(last_event) where sum by all streams
                    var tfPosition =
                        positionEvent.Metadata.ParseCheckpointTagJson(default(ProjectionVersion)).Tag.Position;
                    var progress = 100.0f*(link ?? @event).EventNumber/message.LastEventNumber;
                    var pendingEvent = new PendingEvent(@event, positionEvent, tfPosition, progress);
                    queue.Enqueue(pendingEvent);
                }
            }

            private Queue<PendingEvent> GetStreamQueue(EventRecord positionEvent)
            {
                Queue<PendingEvent> queue;
                if (!_buffers.TryGetValue(positionEvent.EventStreamId, out queue))
                {
                    queue = new Queue<PendingEvent>();
                    _buffers.Add(positionEvent.EventStreamId, queue);
                }
                return queue;
            }

            private void CheckSwitch()
            {
                if (_eventByTypeIndexEventReader._disposed) // max N reached
                    return;
                Queue<PendingEvent> q;
                if (
                    _streamToEventType.Keys.All(
                        v =>
                        _eofs[v]
                        || _buffers.TryGetValue(v, out q) && q.Count > 0 && !IsIndexedTfPosition(q.Peek().TfPosition)))
                {
                    _eventByTypeIndexEventReader._readAllMode = true;
                    _eventByTypeIndexEventReader.RequestEventsAll();
                }
            }

            private bool IsIndexedTfPosition(TFPos tfPosition)
            {
                //TODO: ensure <= is acceptable and replace
                return tfPosition < _lastKnownIndexCheckpointPosition;
            }

            private void PauseOrContinueReadingStream(string eventStreamId, bool delay)
            {
                if (_eventByTypeIndexEventReader._disposed) // max N reached
                    return;
                if (_eventByTypeIndexEventReader._pauseRequested)
                    _eventByTypeIndexEventReader._paused = !_eventByTypeIndexEventReader.AreEventsRequested();
                else
                    RequestEvents(eventStreamId, delay);
                _eventByTypeIndexEventReader._publisher.Publish(_eventByTypeIndexEventReader.CreateTickMessage());
            }

            private void ReadIndexCheckpointStreamCompleted(
                ReadStreamResult result, EventStore.Core.Data.ResolvedEvent[] events)
            {
                if (_eventByTypeIndexEventReader._disposed)
                    return;
                if (_eventByTypeIndexEventReader._readAllMode)
                    throw new InvalidOperationException();

                if (!_indexCheckpointStreamRequested)
                    throw new InvalidOperationException("Read index checkpoint has not been requested");
                if (_eventByTypeIndexEventReader._paused)
                    throw new InvalidOperationException("Paused");
                _indexCheckpointStreamRequested = false;
                switch (result)
                {
                    case ReadStreamResult.NoStream:
                        if (_eventByTypeIndexEventReader._pauseRequested)
                            _eventByTypeIndexEventReader._paused = !AreEventsRequested();
                        else
                            RequestCheckpointStream(delay: true);
                        _eventByTypeIndexEventReader._publisher.Publish(
                            _eventByTypeIndexEventReader.CreateTickMessage());
                        break;
                    case ReadStreamResult.Success:
                        if (events.Length != 0)
                        {
                            //NOTE: only one event if backward order was requested
                            foreach (var @event in events)
                            {
                                var data = @event.Event.Data.ParseCheckpointTagJson(default(ProjectionVersion)).Tag;
                                _lastKnownIndexCheckpointPosition = data.Position;
                                _lastKnownIndexCheckpointEventNumber = @event.Event.EventNumber;
                            }
                        }
                        if (_eventByTypeIndexEventReader._disposed)
                            return;

                        if (_eventByTypeIndexEventReader._pauseRequested)
                            _eventByTypeIndexEventReader._paused = !AreEventsRequested();
                        else if (events.Length == 0)
                            RequestCheckpointStream(delay: true);
                        _eventByTypeIndexEventReader._publisher.Publish(
                            _eventByTypeIndexEventReader.CreateTickMessage());
                        break;
                    default:
                        throw new NotSupportedException(
                            string.Format("ReadEvents result code was not recognized. Code: {0}", result));
                }
            }

            private void CheckEof()
            {
                if (_eofs.All(v => v.Value))
                    _eventByTypeIndexEventReader.SendEof();
            }

            private void ProcessBuffers()
            {
                if (_eventByTypeIndexEventReader._disposed) // max N reached
                    return;
                if (_eventByTypeIndexEventReader._readAllMode)
                    throw new InvalidOperationException();
                while (true)
                {
                    var minStreamId = "";
                    var minPosition = new TFPos(long.MaxValue, long.MaxValue);
                    var any = false;
                    var anyEof = false;
                    foreach (var streamId in _streamToEventType.Keys)
                    {
                        Queue<PendingEvent> buffer;
                        _buffers.TryGetValue(streamId, out buffer);

                        if ((buffer == null || buffer.Count == 0))
                            if (_eofs[streamId])
                            {
                                anyEof = true;
                                continue; // eof - will check if it was safe later
                            }
                            else
                                return; // still reading

                        var head = buffer.Peek();
                        var targetEventPosition = GetTargetEventPosition(head);

                        if (targetEventPosition < minPosition)
                        {
                            minPosition = targetEventPosition;
                            minStreamId = streamId;
                            any = true;
                        }
                    }

                    if (!any)
                        break;

                    if (!anyEof || IsIndexedTfPosition(minPosition))
                    {
                        var minHead = _buffers[minStreamId].Dequeue();
                        DeliverEventRetrievedByIndex(
                            minHead.Event, minHead.PositionEvent, minHead.Progress, minPosition);
                    }
                    else
                        return; // no safe events to deliver

                    if (_eventByTypeIndexEventReader.CheckEnough())
                        return;
                }
            }

            public void RequestCheckpointStream(bool delay)
            {
                if (_eventByTypeIndexEventReader._disposed || _eventByTypeIndexEventReader._readAllMode)
                    throw new InvalidOperationException("Disposed or invalid mode");
                if (_eventByTypeIndexEventReader._pauseRequested || _eventByTypeIndexEventReader._paused)
                    throw new InvalidOperationException("Paused or pause requested");
                if (_indexCheckpointStreamRequested)
                    return;

                _indexCheckpointStreamRequested = true;

                Message readRequest;
                if (_lastKnownIndexCheckpointEventNumber == -1)
                {
                    readRequest =
                        new ClientMessage.ReadStreamEventsBackward(
                            _eventByTypeIndexEventReader.EventReaderCorrelationId, new SendToThisEnvelope(this), "$et",
                            -1, 1, false, null, SystemAccount.Principal);
                }
                else
                {
                    readRequest =
                        new ClientMessage.ReadStreamEventsForward(
                            _eventByTypeIndexEventReader.EventReaderCorrelationId, new SendToThisEnvelope(this), "$et",
                            _lastKnownIndexCheckpointEventNumber + 1, 100, false, null, SystemAccount.Principal);
                }
                _eventByTypeIndexEventReader.PublishIORequest(delay, readRequest);
            }

            public void RequestEvents(string stream, bool delay)
            {
                if (_eventByTypeIndexEventReader._disposed || _eventByTypeIndexEventReader._readAllMode)
                    throw new InvalidOperationException("Disposed or invalid mode");
                if (_eventByTypeIndexEventReader._pauseRequested || _eventByTypeIndexEventReader._paused)
                    throw new InvalidOperationException("Paused or pause requested");

                if (_eventsRequested.Contains(stream))
                    return;
                Queue<PendingEvent> queue;
                if (_buffers.TryGetValue(stream, out queue) && queue.Count > 0)
                    return;
                _eventsRequested.Add(stream);

                var readEventsForward =
                    new ClientMessage.ReadStreamEventsForward(
                        _eventByTypeIndexEventReader.EventReaderCorrelationId, new SendToThisEnvelope(this), stream,
                        _fromPositions[stream], EventByTypeIndexEventReader._maxReadCount,
                        _eventByTypeIndexEventReader._resolveLinkTos, null, SystemAccount.Principal);
                _eventByTypeIndexEventReader.PublishIORequest(delay, readEventsForward);
            }

            private void DeliverEventRetrievedByIndex(
                EventRecord @event, EventRecord positionEvent, float progress, TFPos position)
            {
                if (position <= _eventByTypeIndexEventReader._lastEventPosition)
                    return;
                _eventByTypeIndexEventReader._fromTfPosition = position;
                _eventByTypeIndexEventReader._lastEventPosition = position;
                _eventByTypeIndexEventReader._deliveredEvents ++;
                string streamId = positionEvent.EventStreamId;
                //TODO: add event sequence validation for inside the index stream
                _eventByTypeIndexEventReader._publisher.Publish(
                    //TODO: publish both link and event data
                    new ReaderSubscriptionMessage.CommittedEventDistributed(
                        _eventByTypeIndexEventReader.EventReaderCorrelationId,
                        new ResolvedEvent(
                            streamId, positionEvent.EventNumber, @event.EventStreamId, @event.EventNumber, true,
                            position, @event.EventId, @event.EventType, (@event.Flags & PrepareFlags.IsJson) != 0,
                            @event.Data, @event.Metadata, positionEvent.Metadata, positionEvent.TimeStamp),
                        _eventByTypeIndexEventReader._stopOnEof ? (long?) null : positionEvent.LogPosition, progress));
            }

            internal bool AreEventsRequested()
            {
                return _eventsRequested.Count != 0 || _indexCheckpointStreamRequested;
            }

            public void Dispose()
            {
                _disposed = true;
            }
        }

        public class TfBased : IHandle<ClientMessage.ReadAllEventsForwardCompleted>
        {
            private readonly EventByTypeIndexEventReader _eventByTypeIndexEventReader;
            private readonly HashSet<string> _eventTypes;
            private readonly ITimeProvider _timeProvider;
            public bool _tfEventsRequested;
            private bool _disposed;

            public TfBased(ITimeProvider timeProvider, EventByTypeIndexEventReader eventByTypeIndexEventReader)
            {
                _timeProvider = timeProvider;
                _eventTypes = eventByTypeIndexEventReader._eventTypes;
                _eventByTypeIndexEventReader = eventByTypeIndexEventReader;
            }

            public void Handle(ClientMessage.ReadAllEventsForwardCompleted message)
            {
                if (_eventByTypeIndexEventReader._disposed)
                    return;
                if (!_eventByTypeIndexEventReader._readAllMode)
                    throw new InvalidOperationException();

                if (!_tfEventsRequested)
                    throw new InvalidOperationException("TF events has not been requested");
                if (_eventByTypeIndexEventReader._paused)
                    throw new InvalidOperationException("Paused");
                _tfEventsRequested = false;
                switch (message.Result)
                {
                    case ReadAllResult.Success:
                        var eof = message.Events.Length == 0;
                        var willDispose = _eventByTypeIndexEventReader._stopOnEof && eof;
                        _eventByTypeIndexEventReader._fromTfPosition = message.NextPos;

                        if (!willDispose)
                        {
                            if (_eventByTypeIndexEventReader._pauseRequested)
                                _eventByTypeIndexEventReader._paused = true;
                                // !AreEventsRequested(); -- we are the only reader
                            else if (eof)
                                RequestTfEvents(delay: true);
                            else
                                _eventByTypeIndexEventReader.RequestEvents();
                        }

                        if (eof)
                        {
                            // the end
                            //TODO: is it safe to pass NEXT as last commit position here
                            DeliverLastCommitPosition(message.NextPos);
                            // allow joining heading distribution
                            SendIdle();
                            _eventByTypeIndexEventReader.SendEof();
                        }
                        else
                        {
                            foreach (var @event in message.Events)
                            {
                                var byStream = @event.Link != null
                                               && _eventByTypeIndexEventReader._indexBased._streamToEventType
                                                                              .ContainsKey(@event.Link.EventStreamId);
                                var byEvent = @event.Link == null && _eventTypes.Contains(@event.Event.EventType);
                                if (byStream) // ignore data just update positions
                                    _eventByTypeIndexEventReader.UpdateNextStreamPosition(
                                        @event.Link.EventStreamId, @event.Link.EventNumber + 1);
                                else if (byEvent)
                                {
                                    DeliverEventRetrievedFromTf(
                                        @event.Event, 100.0f*@event.Event.LogPosition/message.TfEofPosition,
                                        @event.OriginalPosition.Value);
                                }
                                if (_eventByTypeIndexEventReader.CheckEnough())
                                    return;
                            }
                        }
                        if (_eventByTypeIndexEventReader._disposed)
                            return;

                        _eventByTypeIndexEventReader._publisher.Publish(
                            _eventByTypeIndexEventReader.CreateTickMessage());
                        break;
                    default:
                        throw new NotSupportedException(
                            string.Format("ReadEvents result code was not recognized. Code: {0}", message.Result));
                }
            }

            public void RequestTfEvents(bool delay)
            {
                if (_eventByTypeIndexEventReader._disposed || !_eventByTypeIndexEventReader._readAllMode)
                    throw new InvalidOperationException("Disposed or invalid mode");
                if (_eventByTypeIndexEventReader._pauseRequested || _eventByTypeIndexEventReader._paused)
                    throw new InvalidOperationException("Paused or pause requested");
                if (_tfEventsRequested)
                    return;

                _tfEventsRequested = true;
                //TODO: we do not need resolve links, but lets check first with
                var readRequest =
                    new ClientMessage.ReadAllEventsForward(
                        _eventByTypeIndexEventReader.EventReaderCorrelationId, new SendToThisEnvelope(this),
                        _eventByTypeIndexEventReader._fromTfPosition.CommitPosition,
                        _eventByTypeIndexEventReader._fromTfPosition.PreparePosition == -1
                            ? 0
                            : _eventByTypeIndexEventReader._fromTfPosition.PreparePosition, 111, true, null,
                        SystemAccount.Principal);
                _eventByTypeIndexEventReader.PublishIORequest(delay, readRequest);
            }

            private void DeliverLastCommitPosition(TFPos lastPosition)
            {
                if (_eventByTypeIndexEventReader._stopOnEof || _eventByTypeIndexEventReader._stopAfterNEvents != null)
                    return;
                _eventByTypeIndexEventReader._publisher.Publish(
                    new ReaderSubscriptionMessage.CommittedEventDistributed(
                        _eventByTypeIndexEventReader.EventReaderCorrelationId, null, lastPosition.PreparePosition,
                        100.0f));
                //TODO: check was is passed here
            }

            private void DeliverEventRetrievedFromTf(EventRecord @event, float progress, TFPos position)
            {
                if (position <= _eventByTypeIndexEventReader._lastEventPosition)
                    return;
                _eventByTypeIndexEventReader._lastEventPosition = position;
                _eventByTypeIndexEventReader._deliveredEvents ++;
                _eventByTypeIndexEventReader._publisher.Publish(
                    //TODO: publish both link and event data
                    new ReaderSubscriptionMessage.CommittedEventDistributed(
                        _eventByTypeIndexEventReader.EventReaderCorrelationId,
                        new ResolvedEvent(
                            @event.EventStreamId, @event.EventNumber, @event.EventStreamId, @event.EventNumber, false,
                            position, @event.EventId, @event.EventType, (@event.Flags & PrepareFlags.IsJson) != 0,
                            @event.Data, @event.Metadata, null, @event.TimeStamp),
                        _eventByTypeIndexEventReader._stopOnEof ? (long?) null : position.PreparePosition, progress));
            }

            private void SendIdle()
            {
                _eventByTypeIndexEventReader._publisher.Publish(
                    new ReaderSubscriptionMessage.EventReaderIdle(
                        _eventByTypeIndexEventReader.EventReaderCorrelationId, _timeProvider.Now));
            }

            public void Dispose()
            {
                _disposed = true;
            }
        }
    }
}
