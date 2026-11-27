namespace RainMeadow
{
    public abstract partial class OnlineEntity
    {
        internal void RequestIfAvailable()
        {
            if (!isMine && isTransferable && !isPending &&
                currentlyJoinedResource is { isAvailable: true, isReleasing: false })
            {
                Request();
            }
        }

        // I request, to someone else
        public void Request()
        {
            RainMeadow.Debug(this);
            if (isMine) throw new InvalidProgrammerException("this entity is already mine");
            if (!isTransferable) throw new InvalidProgrammerException("cannot be transfered");
            if (isPending) throw new InvalidProgrammerException("this entity has a pending request");
            var joined = currentlyJoinedResource;
            if (joined is not { isAvailable: true }) throw new InvalidProgrammerException("in unavailable resource");
            if (!owner.hasLeft && joined.participants.Contains(owner))
            {
                isTransfering = true;
                pendingRequest = owner.InvokeRPC(this.Requested).Then(this.ResolveRequest);
            }
            else if (primaryResource?.owner is { hasLeft: false } supervisor)
            {
                isTransfering = true;
                pendingRequest = supervisor.InvokeRPC(Requested).Then(ResolveRequest);
            }
            else
            {
                RainMeadow.Debug("Not transfering");
            }
        }

        // I've been requested and I'll pass the entity on
        [RPCMethod]
        public void Requested(RPCEvent request)
        {
            RainMeadow.Debug(this);
            RainMeadow.Debug("Requested by : " + request.from.id);
            var primary = primaryResource;
            var joined = currentlyJoinedResource;
            if (isTransferable && !isPending && !request.from.hasLeft &&
                primary is { isActive: true, isReleasing: false, owner: { hasLeft: false } } &&
                joined is { isAvailable: true, isReleasing: false } &&
                (isMine || (primary.isOwner && (owner.hasLeft || !joined.participants.Contains(owner)))))
            {
                primary.LocalEntityTransfered(this, request.from);
                request.from.QueueEvent(new GenericResult.Ok(request));
            }
            else
            {
                request.from.QueueEvent(new GenericResult.Error(request));
            }
        }

        // my request has been answered to
        public virtual void ResolveRequest(GenericResult requestResult)
        {
            RainMeadow.Debug(this);
            if (pendingRequest == null || requestResult.referencedEvent != pendingRequest) return;
            pendingRequest = null;
            if (requestResult is GenericResult.Ok) // I'm the new owner of this entity
            {
                // no op, comes as state in the same tick
            }
            else if (requestResult is GenericResult.Error) // Something went wrong, I should retry
            {
                // todo retry logic
                RainMeadow.Error("request failed for " + this);
                isTransfering = false;
            }
            if (isMine) JoinOrLeavePending(); // keep ticking
        }

        // I release this entity (to room host or world host)
        public void Release()
        {
            RainMeadow.Debug(this);
            if (!isMine) throw new InvalidProgrammerException("not mine");
            if (!isTransferable) throw new InvalidProgrammerException("cannot be transfered");
            if (isPending) throw new InvalidProgrammerException("this entity has a pending request");
            if (primaryResource is null) return; // deactivated

            if (primaryResource.isOwner)
            {
                RainMeadow.Debug("Staying as supervisor");
            }
            else if (primaryResource.owner is { hasLeft: false } supervisor)
            {
                isTransfering = true;
                pendingRequest = supervisor.InvokeRPC(Released, currentlyJoinedResource).Then(ResolveRelease);
            }
        }

        // someone released "to me"
        [RPCMethod]
        public void Released(RPCEvent rpcEvent, OnlineResource inResource)
        {
            RainMeadow.Debug(this);
            RainMeadow.Debug("Released by : " + rpcEvent.from.id);
            var primary = primaryResource;
            if (isTransferable && !isPending && owner == rpcEvent.from &&
                primary is { isOwner: true, isActive: true, isReleasing: false })
            {
                if (inResource is { owner: { hasLeft: false } } &&
                    (inResource.isAvailable || inResource.super.isActive))
                {
                    if (owner != inResource.owner) primary.LocalEntityTransfered(this, inResource.owner);
                }
                else if (!isMine)
                {
                    primary.LocalEntityTransfered(this, OnlineManager.mePlayer);
                }
                rpcEvent.from.QueueEvent(new GenericResult.Ok(rpcEvent));
            }
            else
            {
                rpcEvent.from.QueueEvent(new GenericResult.Error(rpcEvent));
            }
        }

        // got an answer back from my release
        public void ResolveRelease(GenericResult result)
        {
            RainMeadow.Debug(this);
            if (result.referencedEvent != pendingRequest)
            {
                RainMeadow.Error($"Weird event situation, pending is {pendingRequest} and referenced is {result.referencedEvent}");
                return;
            }
            pendingRequest = null;

            if (result is GenericResult.Error)
            {
                isTransfering = false;
                // Aborting a release during disconnect must not recreate it in the outgoing queue.
                if (!result.referencedEvent.aborted)
                {
                    RainMeadow.Error("request failed for " + this);
                    if (isTransferable && isMine && !isPending)
                    {
                        Release();
                    }
                }
            }
            if (isMine) JoinOrLeavePending(); // keep ticking
        }
    }
}
