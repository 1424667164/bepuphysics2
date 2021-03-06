﻿using BepuUtilities;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BepuPhysics.Constraints.Contact
{

    public struct PenetrationLimitProjection
    {
        //Note that these are just the raw jacobians, no precomputation with the JT*EffectiveMass.
        public Vector3Wide AngularA;
        public Vector3Wide AngularB;
        public Vector<float> EffectiveMass;
        public Vector<float> BiasVelocity;
    }



    /// <summary>
    /// Four convex-sourced contact penetration limits solved together. Internally implemented using SI solver. 
    /// Batching saves on redundant data.
    /// </summary>
    public static class PenetrationLimit4
    {
        /// <summary>
        /// Data required to project world space velocities into a constraint impulse.
        /// </summary>
        public struct Projection
        {
            //Note that the data is interleaved to match the access order. We solve each constraint one at a time internally.
            //Also, the normal and inertias are shared across all constraints.
            public PenetrationLimitProjection Penetration0;
            public Vector<float> SoftnessImpulseScale;
            public PenetrationLimitProjection Penetration1;
            public PenetrationLimitProjection Penetration2;
            public PenetrationLimitProjection Penetration3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Prestep(ref BodyInertias inertiaA, ref BodyInertias inertiaB, ref Vector3Wide normal, ref Contact4PrestepData prestep, float dt, float inverseDt,
            out Projection projection)
        {
            //We directly take the prestep data here since the jacobians and error don't undergo any processing.

            //The contact penetration constraint takes the form:
            //dot(positionA + offsetA, N) >= dot(positionB + offsetB, N)
            //Or:
            //dot(positionA + offsetA, N) - dot(positionB + offsetB, N) >= 0
            //dot(positionA + offsetA - positionB - offsetB, N) >= 0
            //where positionA and positionB are the center of mass positions of the bodies offsetA and offsetB are world space offsets from the center of mass to the contact,
            //and N is a unit length vector calibrated to point from B to A. (The normal pointing direction is important; it changes the sign.)
            //In practice, we'll use the collision detection system's penetration depth instead of trying to recompute the error here.

            //So, treating the normal as constant, the velocity constraint is:
            //dot(d/dt(positionA + offsetA - positionB - offsetB), N) >= 0
            //dot(linearVelocityA + d/dt(offsetA) - linearVelocityB - d/dt(offsetB)), N) >= 0
            //The velocity of the offsets are defined by the angular velocity.
            //dot(linearVelocityA + angularVelocityA x offsetA - linearVelocityB - angularVelocityB x offsetB), N) >= 0
            //dot(linearVelocityA, N) + dot(angularVelocityA x offsetA, N) - dot(linearVelocityB, N) - dot(angularVelocityB x offsetB), N) >= 0
            //Use the properties of the scalar triple product:
            //dot(linearVelocityA, N) + dot(offsetA x N, angularVelocityA) - dot(linearVelocityB, N) - dot(offsetB x N, angularVelocityB) >= 0
            //Bake in the negations:
            //dot(linearVelocityA, N) + dot(offsetA x N, angularVelocityA) + dot(linearVelocityB, -N) + dot(-offsetB x N, angularVelocityB) >= 0
            //A x B = -B x A:
            //dot(linearVelocityA, N) + dot(offsetA x N, angularVelocityA) + dot(linearVelocityB, -N) + dot(N x offsetB, angularVelocityB) >= 0
            //And there you go, the jacobians!
            //linearA: N
            //angularA: offsetA x N
            //linearB: -N
            //angularB: N x offsetB
            //Note that we leave the penetration depth as is, even when it's negative. Speculative contacts!
            Vector3Wide.CrossWithoutOverlap(ref prestep.OffsetA0, ref normal, out projection.Penetration0.AngularA);
            Vector3Wide.Subtract(ref prestep.OffsetA0, ref prestep.OffsetB, out var offsetB0);
            Vector3Wide.CrossWithoutOverlap(ref normal, ref offsetB0, out projection.Penetration0.AngularB);
            Vector3Wide.CrossWithoutOverlap(ref prestep.OffsetA1, ref normal, out projection.Penetration1.AngularA);
            Vector3Wide.Subtract(ref prestep.OffsetA1, ref prestep.OffsetB, out var offsetB1);
            Vector3Wide.CrossWithoutOverlap(ref normal, ref offsetB1, out projection.Penetration1.AngularB);
            Vector3Wide.CrossWithoutOverlap(ref prestep.OffsetA2, ref normal, out projection.Penetration2.AngularA);
            Vector3Wide.Subtract(ref prestep.OffsetA2, ref prestep.OffsetB, out var offsetB2);
            Vector3Wide.CrossWithoutOverlap(ref normal, ref offsetB2, out projection.Penetration2.AngularB);
            Vector3Wide.CrossWithoutOverlap(ref prestep.OffsetA3, ref normal, out projection.Penetration3.AngularA);
            Vector3Wide.Subtract(ref prestep.OffsetA3, ref prestep.OffsetB, out var offsetB3);
            Vector3Wide.CrossWithoutOverlap(ref normal, ref offsetB3, out projection.Penetration3.AngularB);

            //effective mass
            Triangular3x3Wide.VectorSandwich(ref projection.Penetration0.AngularA, ref inertiaA.InverseInertiaTensor, out var angularA0);
            Triangular3x3Wide.VectorSandwich(ref projection.Penetration0.AngularB, ref inertiaB.InverseInertiaTensor, out var angularB0);
            Triangular3x3Wide.VectorSandwich(ref projection.Penetration1.AngularA, ref inertiaA.InverseInertiaTensor, out var angularA1);
            Triangular3x3Wide.VectorSandwich(ref projection.Penetration1.AngularB, ref inertiaB.InverseInertiaTensor, out var angularB1);
            Triangular3x3Wide.VectorSandwich(ref projection.Penetration2.AngularA, ref inertiaA.InverseInertiaTensor, out var angularA2);
            Triangular3x3Wide.VectorSandwich(ref projection.Penetration2.AngularB, ref inertiaB.InverseInertiaTensor, out var angularB2);
            Triangular3x3Wide.VectorSandwich(ref projection.Penetration3.AngularA, ref inertiaA.InverseInertiaTensor, out var angularA3);
            Triangular3x3Wide.VectorSandwich(ref projection.Penetration3.AngularB, ref inertiaB.InverseInertiaTensor, out var angularB3);

            //Linear effective mass contribution notes:
            //1) The J * M^-1 * JT can be reordered to J * JT * M^-1 for the linear components, since M^-1 is a scalar and dot(n * scalar, n) = dot(n, n) * scalar.
            //2) dot(normal, normal) == 1, so the contribution from each body is just its inverse mass.
            SpringSettings.ComputeSpringiness(ref prestep.SpringSettings, dt,  out var positionErrorToVelocity, out var effectiveMassCFMScale, out projection.SoftnessImpulseScale);
            var linear = inertiaA.InverseMass + inertiaB.InverseMass;
            //Note that we don't precompute the JT * effectiveMass term. Since the jacobians are shared, we have to do that multiply anyway.
            projection.Penetration0.EffectiveMass = effectiveMassCFMScale / (linear + angularA0 + angularB0);
            projection.Penetration1.EffectiveMass = effectiveMassCFMScale / (linear + angularA1 + angularB1);
            projection.Penetration2.EffectiveMass = effectiveMassCFMScale / (linear + angularA2 + angularB2);
            projection.Penetration3.EffectiveMass = effectiveMassCFMScale / (linear + angularA3 + angularB3);

            //If depth is negative, the bias velocity will permit motion up until the depth hits zero. This works because positionErrorToVelocity * dt will always be <=1.
            var inverseDtVector = new Vector<float>(inverseDt);
            projection.Penetration0.BiasVelocity = Vector.Min(prestep.PenetrationDepth0 * inverseDtVector, Vector.Min(prestep.PenetrationDepth0 * positionErrorToVelocity, prestep.MaximumRecoveryVelocity));
            projection.Penetration1.BiasVelocity = Vector.Min(prestep.PenetrationDepth1 * inverseDtVector, Vector.Min(prestep.PenetrationDepth1 * positionErrorToVelocity, prestep.MaximumRecoveryVelocity));
            projection.Penetration2.BiasVelocity = Vector.Min(prestep.PenetrationDepth2 * inverseDtVector, Vector.Min(prestep.PenetrationDepth2 * positionErrorToVelocity, prestep.MaximumRecoveryVelocity));
            projection.Penetration3.BiasVelocity = Vector.Min(prestep.PenetrationDepth3 * inverseDtVector, Vector.Min(prestep.PenetrationDepth3 * positionErrorToVelocity, prestep.MaximumRecoveryVelocity));
        }


        /// <summary>
        /// Transforms an impulse from constraint space to world space, uses it to modify the cached world space velocities of the bodies.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ApplyImpulse(ref PenetrationLimitProjection projection, ref BodyInertias inertiaA, ref BodyInertias inertiaB, ref Vector3Wide normal,
            ref Vector<float> correctiveImpulse,
            ref BodyVelocities wsvA, ref BodyVelocities wsvB)
        {
            var linearVelocityChangeA = correctiveImpulse * inertiaA.InverseMass;
            Vector3Wide.Scale(ref normal, ref linearVelocityChangeA, out var correctiveVelocityALinearVelocity);
            Vector3Wide.Scale(ref projection.AngularA, ref correctiveImpulse, out var correctiveAngularImpulseA);
            Triangular3x3Wide.TransformBySymmetricWithoutOverlap(ref correctiveAngularImpulseA, ref inertiaA.InverseInertiaTensor, out var correctiveVelocityAAngularVelocity);

            var linearVelocityChangeB = correctiveImpulse * inertiaB.InverseMass;
            Vector3Wide.Scale(ref normal, ref linearVelocityChangeB, out var correctiveVelocityBLinearVelocity);
            Vector3Wide.Scale(ref projection.AngularB, ref correctiveImpulse, out var correctiveAngularImpulseB);
            Triangular3x3Wide.TransformBySymmetricWithoutOverlap(ref correctiveAngularImpulseB, ref inertiaB.InverseInertiaTensor, out var correctiveVelocityBAngularVelocity);

            Vector3Wide.Add(ref wsvA.Linear, ref correctiveVelocityALinearVelocity, out wsvA.Linear);
            Vector3Wide.Add(ref wsvA.Angular, ref correctiveVelocityAAngularVelocity, out wsvA.Angular);
            Vector3Wide.Subtract(ref wsvB.Linear, ref correctiveVelocityBLinearVelocity, out wsvB.Linear); //Note subtract; normal = -jacobianLinearB
            Vector3Wide.Add(ref wsvB.Angular, ref correctiveVelocityBAngularVelocity, out wsvB.Angular);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WarmStart(
            ref Projection projection, ref BodyInertias inertiaA, ref BodyInertias inertiaB, ref Vector3Wide normal,
            ref Vector<float> accumulatedImpulse0,
            ref Vector<float> accumulatedImpulse1,
            ref Vector<float> accumulatedImpulse2,
            ref Vector<float> accumulatedImpulse3, ref BodyVelocities wsvA, ref BodyVelocities wsvB)
        {
            ApplyImpulse(ref projection.Penetration0, ref inertiaA, ref inertiaB, ref normal, ref accumulatedImpulse0, ref wsvA, ref wsvB);
            ApplyImpulse(ref projection.Penetration1, ref inertiaA, ref inertiaB, ref normal, ref accumulatedImpulse1, ref wsvA, ref wsvB);
            ApplyImpulse(ref projection.Penetration2, ref inertiaA, ref inertiaB, ref normal, ref accumulatedImpulse2, ref wsvA, ref wsvB);
            ApplyImpulse(ref projection.Penetration3, ref inertiaA, ref inertiaB, ref normal, ref accumulatedImpulse3, ref wsvA, ref wsvB);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ComputeCorrectiveImpulse(ref BodyVelocities wsvA, ref BodyVelocities wsvB,
            ref PenetrationLimitProjection projection,
            ref Vector3Wide normal, ref Vector<float> softnessImpulseScale,
            ref Vector<float> accumulatedImpulse, out Vector<float> correctiveCSI)
        {
            //Note that we do NOT use pretransformed jacobians here; the linear jacobian sharing (normal) meant that we had the effective mass anyway.
            Vector3Wide.Dot(ref wsvA.Linear, ref normal, out var csvaLinear);
            Vector3Wide.Dot(ref wsvA.Angular, ref projection.AngularA, out var csvaAngular);
            Vector3Wide.Dot(ref wsvB.Linear, ref normal, out var negatedCSVBLinear);
            Vector3Wide.Dot(ref wsvB.Angular, ref projection.AngularB, out var csvbAngular);
            //Compute negated version to avoid the need for an explicit negate.
            var negatedCSI = accumulatedImpulse * softnessImpulseScale + (csvaLinear - negatedCSVBLinear + csvaAngular + csvbAngular - projection.BiasVelocity) * projection.EffectiveMass;

            var previousAccumulated = accumulatedImpulse;
            accumulatedImpulse = Vector.Max(Vector<float>.Zero, accumulatedImpulse - negatedCSI);

            correctiveCSI = accumulatedImpulse - previousAccumulated;

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Solve(ref Projection projection, ref BodyInertias inertiaA, ref BodyInertias inertiaB, ref Vector3Wide normal,
            ref Vector<float> accumulatedImpulse0,
            ref Vector<float> accumulatedImpulse1,
            ref Vector<float> accumulatedImpulse2,
            ref Vector<float> accumulatedImpulse3, ref BodyVelocities wsvA, ref BodyVelocities wsvB)
        {
            ComputeCorrectiveImpulse(ref wsvA, ref wsvB, ref projection.Penetration0, ref normal, ref projection.SoftnessImpulseScale, ref accumulatedImpulse0, out var correctiveCSI0);
            ApplyImpulse(ref projection.Penetration0, ref inertiaA, ref inertiaB, ref normal, ref correctiveCSI0, ref wsvA, ref wsvB);
            ComputeCorrectiveImpulse(ref wsvA, ref wsvB, ref projection.Penetration1, ref normal, ref projection.SoftnessImpulseScale, ref accumulatedImpulse1, out var correctiveCSI1);
            ApplyImpulse(ref projection.Penetration1, ref inertiaA, ref inertiaB, ref normal, ref correctiveCSI1, ref wsvA, ref wsvB);
            ComputeCorrectiveImpulse(ref wsvA, ref wsvB, ref projection.Penetration2, ref normal, ref projection.SoftnessImpulseScale, ref accumulatedImpulse2, out var correctiveCSI2);
            ApplyImpulse(ref projection.Penetration2, ref inertiaA, ref inertiaB, ref normal, ref correctiveCSI2, ref wsvA, ref wsvB);
            ComputeCorrectiveImpulse(ref wsvA, ref wsvB, ref projection.Penetration3, ref normal, ref projection.SoftnessImpulseScale, ref accumulatedImpulse3, out var correctiveCSI3);
            ApplyImpulse(ref projection.Penetration3, ref inertiaA, ref inertiaB, ref normal, ref correctiveCSI3, ref wsvA, ref wsvB);
        }

    }
}
