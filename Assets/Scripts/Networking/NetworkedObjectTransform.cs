﻿using System.Collections.Generic;
using System.IO;
using System;
using UnityEngine;
using System.Linq;
using MLAPI;
using MLAPI.Serialization;

/// <summary>
/// A slightly better prototype component for syncing transforms
/// </summary>
public class NetworkedObjectTransform : NetworkedBehaviour {

    /// <summary>
    /// The base amount of sends per seconds to use when range is disabled
    /// </summary>
    [Range(0, 120)]
    public float FixedSendsPerSecond = 20f;
    /// <summary>
    /// Enable interpolation
    /// </summary>
    [Tooltip("This requires AssumeSyncedSends to be true")]
    public bool InterpolatePosition = true;
    /// <summary>
    /// Should the server interpolate
    /// </summary>
    public bool InterpolateServer = true;

    # region new vars
    private const string POS_CHANNEL = "MLAPI_DEFAULT_MESSAGE";
    private TransformPacket? lastReceivedPacket = null;
    private (Vector3, Quaternion, uint, int)? lastReceivedInfo;
    private (Vector3, Quaternion, uint, int)? lastSentInfo;
    private float lastSendTime;
    private float lastReceiveTime;
    private float lastRecieveClientTime;
    private InterpBuffer<Vector3> position_buffer = new InterpBuffer<Vector3>(transform_function: (Transform t, Vector3 v) => { return t.TransformPoint(v); });
    private InterpBuffer<Quaternion> rotation_buffer = new InterpBuffer<Quaternion>();
    private FloatBuffer latency_buffer = new FloatBuffer(20);
    private float latency = 0f;
    private string UPDATE_TIMER;
    [Tooltip("The delay in seconds buffered for interpolation")]
    public float interp_delay = 0.1f;
    public float inactive_delay = 0.5f;
    public Vector3 velocity;
    # endregion

    /// <summary>
    /// The delegate used to check if a move is valid
    /// </summary>
    /// <param name="oldPos">The previous position</param>
    /// <param name="newPos">The new requested position</param>
    /// <returns>Returns wheter or not the move is valid</returns>
    public delegate bool MoveValidationDelegate(Vector3 oldPos, Vector3 newPos);
    /// <summary>
    /// If set, moves will only be accepted if the custom delegate returns true
    /// </summary>
    public MoveValidationDelegate IsMoveValidDelegate = null;

    private void Awake() {
        UPDATE_TIMER = "NetworkedTransformUpdate_" + gameObject.GetInstanceID().ToString();
    }

    private void OnValidate() {
        if (InterpolateServer && !InterpolatePosition)
            InterpolateServer = false;
    }

    public override void OnDestroyed() {
        Utilities.Instance.RemoveTimer(UPDATE_TIMER);
    }

    /// <summary>
    /// Registers message handlers
    /// </summary>
    public override void NetworkStart() {
        lastSendTime = NetworkingManager.singleton.NetworkTime;
        if (isOwner) {
            Utilities.Instance.CreateTimer(UPDATE_TIMER, inactive_delay);
        }
        else if (!isServer) {
            InvokeServerRpc(RequestTransform, NetworkingManager.singleton.LocalClientId);
        }
        /*if (isOwner) {
            InvokeRepeating("TransmitPosition", 0f, (1f / FixedSendsPerSecond));
        }*/
    }

    private void TransmitPosition() {
        if (!networkedObject.isSpawned) return;

        (uint parentId, int movingObjectId) = GetParentMovingObjectIds();
        Vector3 relativePos = GetRelativePosition(transform.position, parentId, movingObjectId);

        // Do not transmit if there has been no change for inactive_delay seconds
        if (lastSentInfo != (relativePos, transform.rotation, parentId, movingObjectId)) {
            Utilities.Instance.ResetTimer(UPDATE_TIMER);
            lastSentInfo = (relativePos, transform.rotation, parentId, movingObjectId);
        }
        if (Utilities.Instance.CheckTimer(UPDATE_TIMER)) return;

        using (PooledBitStream stream = PooledBitStream.Get()) {
            using (PooledBitWriter writer = PooledBitWriter.Get(stream)) {
                TransformPacket.WritePacket(
                    NetworkingManager.singleton.NetworkTime,
                    relativePos,
                    transform.rotation,
                    parentId,
                    movingObjectId,
                    writer);

                if (isServer)
                    InvokeClientRpcOnEveryoneExcept(ApplyTransform, OwnerClientId, stream, channel: POS_CHANNEL);
                else
                    InvokeServerRpc(SubmitTransform, stream, channel: POS_CHANNEL);
            }
        }
    }

    private (uint, int) GetParentMovingObjectIds() {
        // If we don't have a parent, there's no way we're on a moving platform
        if (transform.parent == null) return (0, -1);
        // Find the moving platform we are parented to. Skip ourselves if we happen to be a moving platform.
        MovingGeneric parent_moving_obj = transform.parent.GetComponentInParent<MovingGeneric>();
        if (parent_moving_obj == null) return (0, -1);

        return (parent_moving_obj.networkId, parent_moving_obj.GetMovingObjectIndex());
    }

    private void Update() {
        if (!isOwner) {
            //If we are server and interpolation is turned on for server OR we are not server and interpolation is turned on
            if ((isServer && InterpolateServer && InterpolatePosition) || (!isServer && InterpolatePosition)) {
                float interp_time = NetworkingManager.singleton.NetworkTime - latency - interp_delay;
                Vector3 new_pos = position_buffer.Interpolate(interp_time, transform.position, FixedSendsPerSecond, Vector3.Lerp);
                if ((transform.position - new_pos).magnitude > 2f) {
                    Debug.Log("LARGE DIFF IN DISTANCE!");
                }
                transform.position = new_pos;
                transform.rotation = rotation_buffer.Interpolate(interp_time, transform.rotation, FixedSendsPerSecond, Quaternion.Slerp);
            }
        }
        else if (NetworkingManager.singleton.NetworkTime - lastSendTime >= (1f / FixedSendsPerSecond)) {
            lastSendTime = NetworkingManager.singleton.NetworkTime;
            TransmitPosition();
        }
    }

    [ClientRPC]
    private void ApplyTransform(uint clientId, Stream stream) {
        if (!enabled) return;
        TransformPacket received_transform = TransformPacket.FromStream(stream);

        if (InterpolatePosition) {
            lastReceiveTime = NetworkingManager.singleton.NetworkTime;
            if (received_transform.timestamp < lastRecieveClientTime) {
                Debug.Log("OUT OF ORDER PACKET DETECTED");
            }

            if (lastReceivedInfo != null) {
                (Vector3 lastReceivedPosition, Quaternion lastReceivedRotation, uint lastReceivedParentId, int lastReceivedMovingObjectId) = lastReceivedInfo.Value;
                Vector3 world_pos = GetWorldPosition(received_transform.position, received_transform.parentId, received_transform.movingObjectId);
                Vector3 prev_world_pos = GetWorldPosition(lastReceivedPosition, lastReceivedParentId, lastReceivedMovingObjectId);
                // Use local velocity if we are on the same moving platform as the previous packet
                Vector3 new_velocity;
                if ((lastReceivedParentId, lastReceivedMovingObjectId) == (received_transform.parentId, received_transform.movingObjectId)) {
                    new_velocity = (received_transform.position - lastReceivedPosition) / (received_transform.timestamp - lastRecieveClientTime);
                }
                else {
                    new_velocity = (world_pos - prev_world_pos) / (received_transform.timestamp - lastRecieveClientTime);
                }
                if (!float.IsNaN(new_velocity.magnitude) && !float.IsInfinity(new_velocity.magnitude)) velocity = new_velocity;
            }

            lastRecieveClientTime = received_transform.timestamp;
            latency = latency_buffer.Accumulate(lastReceiveTime - lastRecieveClientTime) / latency_buffer.Size();
            Transform parent_transform = GetNetworkedObjectTransform(received_transform.parentId, received_transform.movingObjectId);
            position_buffer.Insert(lastRecieveClientTime, received_transform.position, parent_transform);
            rotation_buffer.Insert(lastRecieveClientTime, received_transform.rotation);
        }
        else {
            transform.position = received_transform.position;
            transform.rotation = received_transform.rotation;
        }
        lastReceivedInfo = (received_transform.position, received_transform.rotation, received_transform.parentId, received_transform.movingObjectId);
    }

    private Transform GetNetworkedObjectTransform(uint netId, int MovingObjectId) {
        // 0 parentId indicates that there is no parent and -1 movingObjectId indicates no moving platform
        if (netId == 0 || MovingObjectId == -1) return null;

        return MovingGeneric.GetMovingObjectAt(netId, MovingObjectId).transform;
    }

    private Vector3 GetWorldPosition(Vector3 relative_pos, uint parentId, int MovingObjectId) {
        // 0 parentId indicates that there is no parent and -1 movingObjectId indicates no moving platform
        if (parentId == 0 || MovingObjectId == -1) return relative_pos;
        // try and get the world position converted from the parent platforms local space
        Transform parent_transform = GetNetworkedObjectTransform(parentId, MovingObjectId);
        if (parent_transform == null) return relative_pos;

        return parent_transform.TransformPoint(relative_pos);
    }

    private Vector3 GetRelativePosition(Vector3 world_pos, uint parentId, int MovingObjectId) {
        // 0 parentId indicates that there is no parent and -1 movingObjectId indicates no moving platform
        if (parentId == 0 || MovingObjectId == -1) return world_pos;
        // try and get the relative position to the parent platform
        Transform parent_transform = GetNetworkedObjectTransform(parentId, MovingObjectId);
        if (parent_transform == null) return world_pos;

        return parent_transform.InverseTransformPoint(world_pos);
    }

    [ServerRPC(RequireOwnership = false)]
    private void RequestTransform(uint clientId) {
        if (!enabled) return;
        if (isOwner) {
            (uint parentId, int movingObjectId) = GetParentMovingObjectIds();
            Vector3 relativePos = GetRelativePosition(transform.position, parentId, movingObjectId);

            using (PooledBitStream writeStream = PooledBitStream.Get()) {
                using (PooledBitWriter writer = PooledBitWriter.Get(writeStream)) {
                    TransformPacket.WritePacket(
                        NetworkingManager.singleton.NetworkTime,
                        relativePos,
                        transform.rotation,
                        parentId,
                        movingObjectId,
                        writer);
                    InvokeClientRpcOnClient(ApplyTransform, clientId, writeStream, channel: POS_CHANNEL);
                }
            }
        }
        else if (lastReceivedPacket != null) {
            using (PooledBitStream writeStream = PooledBitStream.Get()) {
                using (PooledBitWriter writer = PooledBitWriter.Get(writeStream)) {
                    lastReceivedPacket.Value.Write(writer);
                    InvokeClientRpcOnClient(ApplyTransform, clientId, writeStream, channel: POS_CHANNEL);
                }
            }
        }
    }

    [ServerRPC]
    private void SubmitTransform(uint clientId, Stream stream) {
        if (!enabled) return;
        TransformPacket received_transform = TransformPacket.FromStream(stream);
        if (IsMoveValidDelegate != null && !IsMoveValidDelegate(transform.position, received_transform.position)) {
            //Invalid move!
            //TODO: Add rubber band (just a message telling them to go back)
            return;
        }

        lastReceivedPacket = received_transform;
        using (PooledBitStream writeStream = PooledBitStream.Get()) {
            using (PooledBitWriter writer = PooledBitWriter.Get(writeStream)) {
                received_transform.Write(writer);
                InvokeClientRpcOnEveryoneExcept(ApplyTransform, OwnerClientId, writeStream, channel: POS_CHANNEL);
            }
        }
    }

    /// <summary>
    /// Teleports the transform to the given position and rotation
    /// </summary>
    /// <param name="position">The position to teleport to</param>
    /// <param name="rotation">The rotation to teleport to</param>
    public void Teleport(Vector3 position, Quaternion rotation) {
        if (InterpolateServer && isServer || isClient) {
            // Implement when needed
        }
    }
}

public struct TransformPacket {
    public float timestamp;
    public Vector3 position;
    public Quaternion rotation;
    public uint parentId;
    public int movingObjectId;

    public TransformPacket(float timestamp,
                           Vector3 position,
                           Quaternion rotation,
                           uint parentId,
                           int movingObjectId) {
        this.timestamp = timestamp;
        this.position = position;
        this.rotation = rotation;
        this.parentId = parentId;
        this.movingObjectId = movingObjectId;
    }

    public static TransformPacket FromStream(Stream stream) {
        using (PooledBitReader reader = PooledBitReader.Get(stream)) {
            float time = reader.ReadSinglePacked();

            float xPos = reader.ReadSinglePacked();
            float yPos = reader.ReadSinglePacked();
            float zPos = reader.ReadSinglePacked();

            float xRot = reader.ReadSinglePacked();
            float yRot = reader.ReadSinglePacked();
            float zRot = reader.ReadSinglePacked();

            uint pId = reader.ReadUInt32Packed();
            int moId = reader.ReadInt32Packed();

            return new TransformPacket(time, new Vector3(xPos, yPos, zPos), Quaternion.Euler(xRot, yRot, zRot), pId, moId);
        }
    }

    public void Write(PooledBitWriter writer) {
        writer.WriteSinglePacked(timestamp);

        writer.WriteSinglePacked(position.x);
        writer.WriteSinglePacked(position.y);
        writer.WriteSinglePacked(position.z);

        writer.WriteSinglePacked(rotation.eulerAngles.x);
        writer.WriteSinglePacked(rotation.eulerAngles.y);
        writer.WriteSinglePacked(rotation.eulerAngles.z);

        writer.WriteUInt32Packed(parentId);
        writer.WriteInt32Packed(movingObjectId);
    }

    public static void WritePacket(float timestamp,
                                   Vector3 position,
                                   Quaternion rotation,
                                   uint parentId,
                                   int movingObjectId,
                                   PooledBitWriter writer) {
        new TransformPacket(timestamp, position, rotation, parentId, movingObjectId).Write(writer);
    }
}

public class InterpBuffer<T> {
    private class BufferEntry {
        public float time;
        public T value;
        public Transform parent;

        public BufferEntry(float time, T value, Transform parent) {
            this.time = time;
            this.value = value;
            this.parent = parent;
        }
    }

    private List<BufferEntry> buffer;
    private Func<Transform, T, T> transform_function;
    public InterpBuffer(Func<Transform, T, T> transform_function = null) {
        buffer = new List<BufferEntry>();
        this.transform_function = transform_function;
    }

    public void Insert(float time, T value, Transform parent = null) {
        BufferEntry entry = new BufferEntry(time, value, parent);
        // Hardcode a limit on the buffer for now to prevent a leak
        if (buffer.Count > 20) {
            buffer.RemoveAt(0);
        }
        // Check the end to see if we should just append early
        if (buffer.Count == 0 || buffer.Last().time <= time) {
            buffer.Add(entry);
        }
        // Otherwise find where we need to insert this frame
        else {
            int idx = buffer.FindIndex((BufferEntry it) => { return it.time > time; });
            if (idx == -1) {
                buffer.Add(entry);
            }
            else {
                buffer.Insert(idx, entry);
            }
        }
    }

    public T Interpolate(float time, Func<T, T, float, T> interp_function) {
        if (buffer.Count == 0) {
            return default(T);
        }
        int upperboundidx = buffer.FindIndex((BufferEntry it) => { return it.time > time; });
        if (upperboundidx == -1) {
            return ComputeWorldValue(buffer.Last());
        }
        else if (upperboundidx == 0) {
            return ComputeWorldValue(buffer.First());
        }
        BufferEntry upper = buffer[upperboundidx];
        BufferEntry lower = buffer[upperboundidx - 1];
        return interp_function(ComputeWorldValue(lower), ComputeWorldValue(upper), (time - lower.time) / (upper.time - lower.time));
    }

    public T Interpolate(float time, T value, float rate, Func<T, T, float, T> interp_function) {
        // If the buffers empty, just return the current value
        if (buffer.Count == 0) {
            return value;
        }
        int upperboundidx = buffer.FindIndex((BufferEntry it) => { return it.time > time; });
        // If we are too far ahead of the buffer, return the last entry
        if (upperboundidx == -1) {
            return ComputeWorldValue(buffer.Last());
        }
        // If the current value occurs before all values in the buffer, interpolate
        // towards the first value from the current value using the expected rate.
        else if (upperboundidx == 0) {
            BufferEntry entry = buffer.First();
            return interp_function(value, ComputeWorldValue(entry), (entry.time - time) * rate);
        }
        // Otherwise interpolate between the two values surrounding the current value at the current time
        BufferEntry upper = buffer[upperboundidx];
        BufferEntry lower = buffer[upperboundidx - 1];
        return interp_function(ComputeWorldValue(lower), ComputeWorldValue(upper), (time - lower.time) / (upper.time - lower.time));
    }

    private T ComputeWorldValue(BufferEntry entry) {
        T world_value = entry.value;
        if (entry.parent != null && transform_function != null) {
            world_value = transform_function(entry.parent, world_value);
        }
        return world_value;
    }
}