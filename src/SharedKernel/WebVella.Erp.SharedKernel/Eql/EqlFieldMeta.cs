using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Eql
{
	public class EqlFieldMeta
	{
		public string Name { get; set; }

		public Field Field { get; set; } = null;

		public EntityRelation Relation { get; set; } = null;

		public List<EqlFieldMeta> Children { get; private set; } = new List<EqlFieldMeta>();

		public override string ToString()
		{
			return Name;
		}
	}
}
