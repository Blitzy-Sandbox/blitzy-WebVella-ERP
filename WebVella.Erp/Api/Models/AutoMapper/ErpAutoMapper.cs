using AutoMapper;
using AutoMapper.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace WebVella.Erp.Api.Models.AutoMapper
{
	public static class ErpAutoMapper
	{
		public static IMapper Mapper = null;

		public static void Initialize(MapperConfigurationExpression cfg)
		{
			//A06/SCA (GHSA-rvv3-g6hj-g44x): AutoMapper was upgraded from the End-of-Life, High-severity 14.0.0 to the
			//patched 16.1.1 to clear the advisory. From v15 the MapperConfiguration constructor requires an
			//ILoggerFactory; NullLoggerFactory.Instance preserves the previous no-logging behavior with no functional
			//change (the mapping configuration and IMapper contract are otherwise identical).
			Mapper = new Mapper(new MapperConfiguration(cfg, NullLoggerFactory.Instance));
		}
	}
}
