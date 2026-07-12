// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using EnsureThat;
using Ignixa.Serialization.Models;

namespace Microsoft.Health.Fhir.Core.Features.Resources.Bundle
{
    /// <summary>
    /// Carries an Ignixa-native bundle response for zero-copy serialization. Immutable after
    /// construction -- do not mutate any node reachable from this type after building it. See
    /// docs/features/sdk-migration/node-mutation.md for why (this type sidesteps the reuse-vs-rebuild
    /// hazard entirely by never being mutated post-construction).
    ///
    /// Deliberately NOT a subclass of <see cref="BundleJsonNode"/>: BundleJsonNode's serialization is a
    /// non-overridable static extension, so a subclass could not make generic serialization correct --
    /// any code path that grabbed the node and serialized it generically would silently emit a bundle
    /// with no entry bodies. This type can only be serialized by <see cref="IgnixaBundleSerializer"/>.
    ///
    /// WARNING: calling ResourceElement.ToPoco() on the ResourceElement wrapping this carrier does NOT
    /// throw, and does NOT produce an error -- it silently returns a Bundle POCO with the correct
    /// id/meta/type/total/link but a hollow (empty) entry array, since only the skeleton participates in
    /// the typed-element view. Every consumer of this bundle must read it via GetIgnixaRawBundle()
    /// (mirroring GetIgnixaNode()), never via ToPoco()/.Instance. As of this writing, all six production
    /// consumers of IBundleFactory are pure pass-throughs to FhirResult and never call ToPoco() -- verify
    /// this is still true before adding a new consumer.
    /// </summary>
    public sealed class IgnixaRawBundle
    {
        public IgnixaRawBundle(BundleJsonNode skeleton, IReadOnlyList<IgnixaRawBundleEntry> entries)
        {
            EnsureArg.IsNotNull(skeleton, nameof(skeleton));
            EnsureArg.IsNotNull(entries, nameof(entries));

            Skeleton = skeleton;
            Entries = entries;
        }

        /// <summary>The bundle's id/meta/type/total/link properties. Its entry array, if any, is ignored by the serializer -- <see cref="Entries"/> is authoritative.</summary>
        public BundleJsonNode Skeleton { get; }

        public IReadOnlyList<IgnixaRawBundleEntry> Entries { get; }
    }
}
