using AutoMapper;
using AutoMapper.Configuration;
using AutoMapper.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace WebVella.Erp.Api.Models.AutoMapper
{
	public static class ErpAutoMapper
	{
		public static IMapper Mapper = null;

		// Security hardening (OWASP A06 / CWE-674 — uncontrolled recursion).
		// AutoMapper advisory GHSA-rvv3-g6hj-g44x / CVE-2026-32933: a deeply nested or
		// self-referential object graph can drive unbounded recursive mapping and exhaust
		// the stack (denial of service). This is remediated at the dependency level by
		// upgrading AutoMapper to 16.1.1 (the patched release; see WebVella.Erp.csproj),
		// which applies a default MaxDepth of 64 for self-referential types automatically.
		// As defence-in-depth — and to preserve the established runtime behaviour — we also
		// keep an explicit uniform recursion-depth cap (MaxDepth) applied to every map.
		// This Initialize method is the single seal chokepoint for all core, plugin and web
		// maps, so the cap is applied uniformly to the already-registered maps just before
		// the MapperConfiguration is built.
		private const int DefaultMaxMappingDepth = 64;

		public static void Initialize(MapperConfigurationExpression cfg)
		{
			// Apply a default recursion-depth cap to all registered maps before the
			// configuration is sealed. ForAllMaps iterates every TypeMap; MaxDepth bounds
			// the recursion AutoMapper will follow for self-referential / cyclic graphs,
			// neutralizing the CWE-674 denial-of-service vector while preserving normal
			// mapping behavior for the shallow graphs the application actually uses.
			cfg.Internal().ForAllMaps((typeMap, mappingExpression) => mappingExpression.MaxDepth(DefaultMaxMappingDepth));

			// AutoMapper 15.0+ requires an ILoggerFactory on the MapperConfiguration ctor
			// (used only for the library's own license/diagnostic log messages). We are not
			// configured through Microsoft.Extensions.DependencyInjection at this seal point,
			// so we supply NullLoggerFactory explicitly; no logging is required here.
			Mapper = new Mapper(new MapperConfiguration(cfg, NullLoggerFactory.Instance));
		}
	}
}
