﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SuperDumpService.Helpers;
using SuperDumpService.Models;

namespace SuperDumpService.Services {

	public class RelationshipRepository {
		private static SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

		// stores relationships bi-directional. both directions should stay in sync
		private readonly IDictionary<DumpIdentifier, IDictionary<DumpIdentifier, double>> relationShips = new Dictionary<DumpIdentifier, IDictionary<DumpIdentifier, double>>();
		private readonly IRelationshipStorage relationshipStorage;
		private readonly DumpRepository dumpRepo;
		private readonly IOptions<SuperDumpSettings> settings;
		public bool IsPopulated { get; private set; } = false;

		public RelationshipRepository(IRelationshipStorage relationshipStorage, DumpRepository dumpRepo, IOptions<SuperDumpSettings> settings) {
			this.relationshipStorage = relationshipStorage;
			this.dumpRepo = dumpRepo;
			this.settings = settings;
		}

		public async Task Populate() {
			if (!settings.Value.SimilarityDetectionEnabled) return;
			await BlockIfBundleRepoNotReady("RelationshipRepository.Populate");

			await semaphoreSlim.WaitAsync().ConfigureAwait(false);
			try {
				foreach (var dump in dumpRepo.GetAll()) {
					try {
						relationShips[dump.Id] = await relationshipStorage.ReadRelationships(dump.Id);
					} catch (FileNotFoundException) { 
						// ignore.
					} catch (Exception e) {
						Console.WriteLine("error reading relationship file: " + e.ToString());
						relationshipStorage.Wipe(dump.Id);
					}
				}
			} finally {
				IsPopulated = true;
				semaphoreSlim.Release();
			}
		}

		public async Task UpdateSimilarity(DumpIdentifier dumpA, DumpIdentifier dumpB, double similarity) {
			if (!settings.Value.SimilarityDetectionEnabled) return;

			await semaphoreSlim.WaitAsync().ConfigureAwait(false);
			try {
				await UpdateSimilarity0(dumpA, dumpB, similarity);
				await UpdateSimilarity0(dumpB, dumpA, similarity);
			} finally {
				semaphoreSlim.Release();
			}
		}

		private async Task UpdateSimilarity0(DumpIdentifier dumpA, DumpIdentifier dumpB, double similarity) {
			if (relationShips.TryGetValue(dumpA, out IDictionary<DumpIdentifier, double> relationShip)) {
				await UpdateRelationship(dumpA, dumpB, similarity, relationShip);
			} else {
				var dict = new Dictionary<DumpIdentifier, double>();
				await UpdateRelationship(dumpA, dumpB, similarity, dict);
				relationShips[dumpA] = dict;
			}
		}

		private async Task UpdateRelationship(DumpIdentifier dumpA, DumpIdentifier dumpB, double similarity, IDictionary<DumpIdentifier, double> relationShip) {
			relationShip[dumpB] = similarity;

			// update storage
			await relationshipStorage.StoreRelationships(dumpA, relationShip);
		}

		public async Task WipeAll() {
			await semaphoreSlim.WaitAsync().ConfigureAwait(false);
			try {
				foreach (var item in relationShips) {
					relationshipStorage.Wipe(item.Key);
				}
				relationShips.Clear();
			} finally {
				semaphoreSlim.Release();
			}
		}

		public async Task<double> GetRelationShip(DumpIdentifier dumpA, DumpIdentifier dumpB) {
			await semaphoreSlim.WaitAsync().ConfigureAwait(false);
			try {
				if (relationShips.TryGetValue(dumpA, out IDictionary<DumpIdentifier, double> relationShips1)) {
					if (relationShips1.TryGetValue(dumpB, out double rel1)) return rel1;
				}
				if (relationShips.TryGetValue(dumpB, out IDictionary<DumpIdentifier, double> relationShips2)) {
					if (relationShips2.TryGetValue(dumpA, out double rel2)) return rel2;
				}
				return 0;
			} finally {
				semaphoreSlim.Release();
			}
		}

		public async Task<IDictionary<DumpIdentifier, double>> GetRelationShips(DumpIdentifier dumpA) {
			await semaphoreSlim.WaitAsync().ConfigureAwait(false);
			try {
				if (relationShips.TryGetValue(dumpA, out IDictionary<DumpIdentifier, double> relationShips1)) {
					return relationShips1;
				}
				return new Dictionary<DumpIdentifier, double>();
			} finally {
				semaphoreSlim.Release();
			}
		}

		/// <summary>
		/// Blocks until bundleRepo is fully populated.
		/// </summary>
		private async Task BlockIfBundleRepoNotReady(string sourcemethod) {
			if (!dumpRepo.IsPopulated) {
				Console.WriteLine($"{sourcemethod} is blocked because dumpRepo is not yet fully populated...");
				await Utility.BlockUntil(() => dumpRepo.IsPopulated);
				Console.WriteLine($"...continuing {sourcemethod}.");
			}
		}
	}
}
