using System.Data;
using Dapper;

namespace Community_Service.Data
{
    // Postgres не имеет беззнакового 64-битного типа. user_id — uint64 по контракту,
    // храним его в bigint через битовый проброс ulong <-> long (round-trip без потерь
    // на всём диапазоне uint64). Регистрируется в Program через SqlMapper.AddTypeHandler.
    public class UInt64TypeHandler : SqlMapper.TypeHandler<ulong>
    {
        public override ulong Parse(object value) => unchecked((ulong)(long)value);

        public override void SetValue(IDbDataParameter parameter, ulong value)
        {
            parameter.DbType = DbType.Int64;
            parameter.Value = unchecked((long)value);
        }
    }
}
