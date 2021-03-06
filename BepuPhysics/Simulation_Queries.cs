﻿using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Trees;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BepuPhysics
{
    public interface ISweepHitHandler
    {
        bool AllowTest(CollidableReference collidable);
        bool AllowTest(int childA, CollidableReference collidableB, int childB);
        void OnHit(ref float maximumT, float t, in Vector3 normal, CollidableReference collidable);
    }

    partial class Simulation
    {
        //TODO: This is all sensitive to pose precision. If you change broadphase or pose precision, this will have to change.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void GetPoseAndShape(CollidableReference reference, out RigidPose* pose, out TypedIndex shape)
        {
            if (reference.Mobility == CollidableMobility.Static)
            {
                var index = Statics.HandleToIndex[reference.Handle];
                pose = (RigidPose*)Statics.Poses.Memory + index;
                shape = Statics.Collidables[index].Shape;
            }
            else
            {
                ref var location = ref Bodies.HandleToLocation[reference.Handle];
                ref var set = ref Bodies.Sets[location.SetIndex];
                pose = (RigidPose*)set.Poses.Memory + location.Index;
                shape = set.Collidables[location.Index].Shape;
            }
        }
        struct RayHitDispatcher<TRayHitHandler> : IBroadPhaseRayTester where TRayHitHandler : IRayHitHandler
        {
            public Simulation Simulation;
            public TRayHitHandler HitHandler;


            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe void RayTest(CollidableReference reference, RayData* rayData, float* maximumT)
            {
                if (HitHandler.AllowTest(reference))
                {
                    Simulation.GetPoseAndShape(reference, out var pose, out var shape);
                    if (Simulation.Shapes[shape.Type].RayTest(shape.Index, *pose, rayData->Origin, rayData->Direction, out var t, out var normal) && t < *maximumT)
                    {
                        HitHandler.OnRayHit(*rayData, ref *maximumT, t, normal, reference);
                    }
                }
            }
        }

        /// <summary>
        /// Intersects a ray against the simulation.
        /// </summary>
        /// <typeparam name="THitHandler">Type of the callbacks to execute on ray-object intersections.</typeparam>
        /// <param name="origin">Origin of the ray to cast.</param>
        /// <param name="direction">Direction of the ray to cast.</param>
        /// <param name="maximumT">Maximum length of the ray traversal in units of the direction's length.</param>
        /// <param name="hitHandler">callbacks to execute on ray-object intersections.</param>
        /// <param name="id">User specified id of the ray.</param>
        public unsafe void RayCast<THitHandler>(in Vector3 origin, in Vector3 direction, float maximumT, ref THitHandler hitHandler, int id = 0) where THitHandler : IRayHitHandler
        {
            TreeRay.CreateFrom(origin, direction, maximumT, id, out var rayData, out var treeRay);
            RayHitDispatcher<THitHandler> dispatcher;
            dispatcher.HitHandler = hitHandler;
            dispatcher.Simulation = this;
            BroadPhase.RayCast(origin, direction, maximumT, ref dispatcher, id);
        }

        unsafe struct SweepHitDispatcher<TSweepHitHandler> : IBroadPhaseSweepTester, ISweepFilter where TSweepHitHandler : ISweepHitHandler
        {
            public Simulation Simulation;
            public void* ShapeData;
            public int ShapeType;
            public RigidPose Pose;
            public BodyVelocity Velocity;
            public TSweepHitHandler HitHandler;
            public CollidableReference CollidableBeingTested;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool AllowTest(int childA, int childB)
            {
                return HitHandler.AllowTest(childA, CollidableBeingTested, childB);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe void Test(CollidableReference reference, ref float maximumT)
            {
                if (HitHandler.AllowTest(reference))
                {
                    Simulation.GetPoseAndShape(reference, out var targetPose, out var shape);
                    Simulation.Shapes[shape.Type].GetShapeData(shape.Index, out var targetShapeData, out _);
                    //Note that the velocity of the target shape is treated as zero for the purposes of a simulation wide cast.
                    //If you wanted to create a simulation velocity aware sweep, you would want to pull the velocity of the target from the Bodies set for collidable references
                    //that are associated with non-statics. It would look like this:
                    //BodyVelocity targetVelocity;
                    //if (reference.Mobility != CollidableMobility.Static)
                    //{
                    //    ref var location = ref Simulation.Bodies.HandleToLocation[reference.Handle];
                    //    //If the body is inactive, even though they can have small nonzero velocities, you probably should treat it as having zero velocity.
                    //    //Otherwise, you might get some unintuitive results where the sweep integrated the inactive body's velocity forward, but the simulation didn't.
                    //    if (location.SetIndex == 0)
                    //        targetVelocity = Simulation.Bodies.ActiveSet.Velocities[location.Index];
                    //    else
                    //        targetVelocity = new BodyVelocity();
                    //}
                    //else
                    //{
                    //    targetVelocity = new BodyVelocity();
                    //}
                    CollidableBeingTested = reference;
                    var task = Simulation.NarrowPhase.SweepTaskRegistry.GetTask(ShapeType, shape.Type);
                    if (task != null && task.Sweep(
                        ShapeData, ShapeType, Pose, Velocity,
                        targetShapeData, shape.Type, *targetPose, new BodyVelocity(),
                        ref this, out var t, out var normal))
                    {
                        HitHandler.OnHit(ref maximumT, t, normal, reference);
                    }
                }
            }
        }


        /// <summary>
        /// Sweeps a shape against the simulation.
        /// </summary>
        /// <typeparam name="TShape">Type of the shape to sweep.</typeparam>
        /// <typeparam name="TSweepHitHandler">Type of the callbacks executed when a sweep impacts an object in the scene.</typeparam>
        /// <param name="shape">Shape to sweep.</param>
        /// <param name="pose">Starting pose of the sweep.</param>
        /// <param name="velocity">Velocity of the swept shape.</param>
        /// <param name="maximumT">Maximum length of the sweep in units of time used to integrate the velocity.</param>
        /// <param name="hitHandler">Callbacks executed when a sweep impacts an object in the scene.</param>
        /// <remarks>Simulation objects are treated as stationary during the sweep.</remarks>
        public unsafe void Sweep<TShape, TSweepHitHandler>(TShape shape, in RigidPose pose, in BodyVelocity velocity, float maximumT, ref TSweepHitHandler hitHandler)
            where TShape : IConvexShape where TSweepHitHandler : ISweepHitHandler
        {
            //Build a bounding box.
            shape.GetBounds(pose.Orientation, out var maximumRadius, out var maximumAngularExpansion, out var min, out var max);
            BoundingBoxBatcher.ExpandBoundingBox(ref min, ref max, velocity.Angular, maximumT, maximumRadius, maximumAngularExpansion);
            min += pose.Position;
            max += pose.Position;
            var direction = velocity.Linear * maximumT;
            SweepHitDispatcher<TSweepHitHandler> dispatcher;
            dispatcher.HitHandler = hitHandler;
            dispatcher.Pose = pose;
            dispatcher.Velocity = velocity;
            //Note that the shape was passed by copy, and that all shape types are required to be blittable. No GC hole.
            dispatcher.ShapeData = Unsafe.AsPointer(ref shape);
            dispatcher.ShapeType = shape.TypeId;
            dispatcher.Simulation = this;
            dispatcher.CollidableBeingTested = default;
            BroadPhase.Sweep(min, max, direction, maximumT, ref dispatcher);
        }

    }
}
