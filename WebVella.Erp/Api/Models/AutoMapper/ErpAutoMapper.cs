using AutoMapper;
using AutoMapper.Configuration;
using AutoMapper.Internal;
using System;
using System.Collections.Generic;
using System.Text;

namespace WebVella.Erp.Api.Models.AutoMapper
{
	public static class ErpAutoMapper
	{
		public static IMapper Mapper = null;

		// Security hardening (OWASP A06 / CWE-674 — uncontrolled recursion).
		// AutoMapper 14.0.0 carries advisory GHSA-rvv3-g6hj-g44x: a deeply nested or
		// self-referential object graph can drive unbounded recursive mapping and exhaust
		// the stack (denial of service). The advisory's patched releases (15.1.1 / 16.1.1+)
		// are distributed under a paid commercial license and break the static-mapper API
		// used throughout this solution, so an in-place version bump would violate the
		// project's API/functionality preservation and minimal-change constraints.
		// Instead we apply the vendor-recommended runtime mitigation: a default recursion
		// depth cap (MaxDepth) for every map. This Initialize method is the single seal
		// chokepoint for all core, plugin and web maps, so the cap is applied uniformly to
		// the already-registered maps just before the MapperConfiguration is built.
		private const int DefaultMaxMappingDepth = 64;

		public static void Initialize(MapperConfigurationExpression cfg)
		{
			// Apply a default recursion-depth cap to all registered maps before the
			// configuration is sealed. ForAllMaps iterates every TypeMap; MaxDepth bounds
			// the recursion AutoMapper will follow for self-referential / cyclic graphs,
			// neutralizing the CWE-674 denial-of-service vector while preserving normal
			// mapping behavior for the shallow graphs the application actually uses.
			cfg.Internal().ForAllMaps((typeMap, mappingExpression) => mappingExpression.MaxDepth(DefaultMaxMappingDepth));

			Mapper = new Mapper(new MapperConfiguration(cfg));
		}
	}
}
