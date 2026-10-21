# Scratch: July 2026's analysed games, from the index's Parquet rows (flags bit 0 = has [%eval]).
import duckdb, sys
glob = sys.argv[1]
con = duckdb.connect(config={"memory_limit": "1GB", "threads": 2, "temp_directory": sys.argv[2]})
q = f"""
with g as (
  select plies, white_elo, black_elo, flags,
         cast(clock_base as int) + 40 * cast(increment as int) as est
  from read_parquet('{glob}')
)
select
  count(*) as games,
  sum(case when flags & 1 = 1 then 1 else 0 end) as analysed,
  sum(case when flags & 1 = 1 then cast(plies as bigint) else 0 end) as analysed_plies,
  sum(case when flags & 1 = 1 and est >= 180 then 1 else 0 end) as analysed_blitz_up,
  sum(case when flags & 1 = 1 and white_elo >= 2000 and black_elo >= 2000 then 1 else 0 end) as analysed_both_2000,
  sum(case when flags & 1 = 1 and (flags & 12) <> 0 then 1 else 0 end) as analysed_with_a_bot
from g
"""
row = con.execute(q).fetchone()
names = ["games", "analysed", "analysed plies", "analysed, blitz or slower", "analysed, both 2000+", "analysed, a BOT playing"]
for n, v in zip(names, row):
    print(f"{n:28} {v:>14,}")
