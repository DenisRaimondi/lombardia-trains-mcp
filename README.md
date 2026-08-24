# Lombardia Trains MCP

An MCP server that answers questions about Lombardy trains: departure and
arrival boards, live delays, platforms, stop-by-stop progress, cancellations
and crowding — and plans journeys with a change, from the regional timetable
Regione Lombardia publishes as open data. It reads the public ViaggiaTreno
(RFI/Trenitalia) and Trenord APIs. No API key, no account, no scraping.

```
> is the next train to Malpensa on time?

Departures — MILANO CADORNA (S01066), 21:29
  21:23  REG787    SEVESO                       +2'  platform 9
  21:26  REG387    MALPENSA AEROPORTO TERMINA   -2'  platform 1
  21:32  REG887    SARONNO                       0'  platform 6
```

## Install

```bash
dotnet tool install -g LombardiaTrains.Mcp
```

Then register it with your MCP client. For Claude Desktop, in
`claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "lombardia-trains": {
      "command": "lombardia-trains-mcp"
    }
  }
}
```

For Claude Code:

```bash
claude mcp add lombardia-trains -- lombardia-trains-mcp
```

## Tools

| Tool | What it answers |
|---|---|
| `now` | "what time and day is it in Italy?" |
| `search_station` | "what is the station code for Milano Centrale?" |
| `get_departures` | "what is leaving Milano Cadorna on Saturday morning?" |
| `get_arrivals` | "when does the train from Varese get in?" |
| `find_connection` | "which direct trains go from Milano Cadorna to Como?" |
| `find_journey` | "how do I get from Castellanza to Como Lago on Monday morning?" |
| `get_train` | "where is train 4307 right now, and how late is it?" |

Station arguments take either a code (`S01700`) or a name (`milano centrale`), and
times take either `HH:mm` for today or ISO `2026-08-29T09:00` for another day.

Three behaviours exist because the caller is a language model rather than a
person, and each of them prevents a confident wrong answer:

- **`now` exists at all** because a model has no reliable idea of the date in
  Rome, and every relative question — "tonight", "Saturday" — needs one before
  any other tool can be called.
- **Ambiguous names are never resolved silently.** "Milano" matches twenty-six
  stations, and many towns have both a national station and a separate "Nord"
  one served by entirely different trains. The tools return the list and ask, rather than picking the
  first and sounding certain.
- **Unknown places say where coverage ends.** Asking for Lugano returns an
  explanation of what the service covers, so the model can decline instead of
  inventing a train to Switzerland.

`find_connection` finds **direct** trains only, and says so. It reads live
departure boards and each candidate train's stop list rather than a timetable,
so it carries delays and platforms but cannot compose a change.

`find_journey` composes one. It plans from the regional timetable, falls back to
Swiss open data for anywhere that timetable does not reach, and returns
scheduled times with no delays in them — the two tools answer different halves
of the same question and are meant to be used together.

Journeys are ranked by **arrival**, not departure. Someone asking how to get
somewhere wants to be there soonest, and ranking by departure puts a train that
leaves four minutes earlier and arrives forty minutes later at the top of the
list.

Only one change is searched for. Two multiply both the search space and the
ways to be quietly wrong, and on this network almost everything worth reaching
is reachable with one — so the limit is stated rather than hidden behind an
incomplete search.

## The part worth reading

Both upstream APIs are public, undocumented and a little hostile. Most of the
work in this repository is not calling them, it is surviving them. Each of the
following is enforced in code and covered by a test.

### Trenord wants two headers, not one

Trenord answers `403 Forbidden` unless the request carries **both** an `Accept`
header and a `User-Agent`:

| Request | Result |
|---|---|
| no headers | 403 |
| `Accept: */*` only | 403 |
| `User-Agent` only | 403 |
| both | 200 |

This is easy to get half right. Probing the endpoint with curl or Python
suggests that `Accept` alone is enough, because both send a `User-Agent` of
their own without being asked. .NET's `HttpClient` sends neither header unless
told to — so a client that sets only `Accept` keeps getting 403, and finds out
in production rather than on the machine where the call was first tried by hand.

That is the whole reason `TrenordClient` sets both in its constructor.

### Optional fields are absent, not null

`average_crowding`, `average_crowding_label`, `suppression_type` and `alerts`
do not appear in the Trenord payload at all when there is nothing to report.
They are not `null`: the properties are missing.

Verified against five regular services (S5, S11, RE_5, R27): none of them
carried any of the four. Code that assumes these fields exist therefore works
on the trains that have problems and crashes on the ones that do not, which is
the worst possible way round.

### Timestamps come in three different shapes

- Trenord `dep_time` / `arr_time` — local time, `"HH:MM:SS"`. Safe to display.
- Trenord `dep_date_time` / `arr_date_time` — ISO 8601 in **UTC**, with the `Z`.
  Convenient for date arithmetic, wrong if you show it as it is.
- ViaggiaTreno — **epoch milliseconds**, always to be converted declaring
  `Europe/Rome` explicitly. Converting with the machine's local time gives the
  right answer on an Italian laptop and the wrong one on a UTC server.

### ViaggiaTreno wants JavaScript's idea of a date

Board endpoints take the timestamp spelled the way `Date.toString()` spells it:

```
Mon Aug 10 2026 20:20:00 GMT+0200
```

Day and month names must be English. They are built from fixed arrays on
purpose: formatting them through the machine's culture produces `lun ago` on an
Italian system and the endpoint silently returns nothing.

### Two more small ones

- ViaggiaTreno's train lookup answers with **plain text**, not JSON — one line
  per run, with the fields needed by the live-progress endpoint after a `|`.
- It also returns an **empty body** instead of an empty array when there is
  nothing to report, which makes a naive deserializer throw.
- `train_operator` uses `$:$` as its separator, literally.

### The published timetable is not quite GTFS

Regione Lombardia publishes the regional rail timetable under CC0, refreshed
daily. Its `stop_id`s are the same codes ViaggiaTreno uses — `S01136` is the
same station in both — so a planned journey joins to live delays and platforms
with no translation table. That is the good news.

Three things about the published form are not GTFS, and each one fails quietly:

- **Times carry a placeholder date.** `1899-12-31T06:05:00.000` means 06:05.
  GTFS expresses a service running past midnight as `24:05`; this form cannot,
  so it comes back as `00:05` and a train that left at 23:50 appears to arrive
  eighteen hours earlier than it departed. Read literally that makes 00:05 the
  earliest arrival anywhere the last service reaches, and a search for the
  earliest arrival then rejects every real morning connection. Each trip's
  clock is made monotonic before anything is computed from it.
- **`calendar` lost its columns.** Only `service_id` survived publication: the
  weekday flags and the validity dates are gone, so the file cannot be used for
  what it is for. It does not matter, because `calendar_dates` lists every
  service-date pair explicitly rather than as exceptions to a weekly pattern.
- **The two files name services differently.** `trips` writes
  `124865-0b0cb949`, `calendar_dates` writes `124865-2026-08-21-2026-08-30`:
  the same service, suffixed with a hash in one export and with its validity
  period in the other. Joining on the full string matches nothing at all — not
  an error, just an empty result — so both sides are cut back to the number
  they share.

Dates in `calendar_dates` are strings shaped `20260829`. Querying for
`2026-08-29` returns zero rows rather than complaining.

Two more, found by planning real journeys and comparing the answers:

- **A train appears once per stopping pattern it has ever had.** `trips` carries
  `1900025-5d11ed45` and `1900025-5299410e` — the same 08:52 to Varese, one
  calling at twelve stops and one at fifteen. **1051 of 4715 services** have more
  than one variant, up to eight. The calendar knows only the service number, and
  the hash appears nowhere else, so nothing published says which variant runs
  today. Uncollapsed, the planner offered the same departure three times over, a
  minute apart, as though they were a choice. They are reduced to one, keeping
  the latest arrival: pessimistic by a minute rather than promising a train that
  gets there sooner than it will.
- **Nearly a quarter of the feed is not trains.** 1489 of 6265 trips run on route
  `TN_Bus`, "TN Bus sostitutivi" — replacement coaches, `route_type` 3 where rail
  is 2. They are the real service on the day they run, so dropping them would be
  worse than keeping them, but a coach leaves from the forecourt rather than a
  platform, does not appear in the live train data, and a nine-minute connection
  onto one is a different proposition. Legs on it are marked `[BUS]`.

### The planner behind the border does not know it is lost

Where the regional timetable does not reach, journeys fall back to Swiss open
data. That planner covers Switzerland and reaches into Italy near the border. It
does not cover the rest of the country — and asked about it, it does not say so.
It matches the name against its own index and answers about whatever it found.

Asked to plan Milano Centrale to **Roma Termini**, it returned a confident,
correctly formatted, two-hour itinerary to *LaCLINIQUE of Switzerland, Locarno,
Via Bossi 2*. Roma Termini to Napoli Centrale became a four-change, four-hour
journey ending at a street address in Lucens, canton Vaud.

Nationality is not the test that catches this: the Swiss index holds Zurich, and
ViaggiaTreno holds Zurich Altstetten, so "are both stations Italian" rejects a
real journey to Zurich while letting the clinic through. The test that works is
whether the answer is about the places that were asked for — one substantial
word in common, accents folded, so *Zurich* matches *Zürich HB* and *Roma
Termini* matches nothing in Locarno. An answer that fails it is discarded rather
than passed on, and the reply names the live tools, which do work for those
stations.

### Where the open data is simply wrong

Planned journeys were compared against the operator's own planner across
thirty-seven routes, and a third source — the live train data — was asked to
settle the cases where the two disagreed. Most matched to the minute. Three did
not, and none of the three is fixable here:

- **Milano Centrale to Bergamo is four minutes short.** The feed times the RE2
  at forty-eight minutes; the operator's planner says fifty-two, and the live
  data for that train agrees with the operator. Same five stops, same departure
  — the last leg into Bergamo is simply timed wrong.
- **A trip can stop short of where the train goes.** S5 11827 runs Varese to
  Milano and beyond; in this feed it ends at Milano Porta Garibaldi. Everything
  reachable by staying on it is therefore invisible, which is why Varese to
  Bergamo comes back an hour worse than the published answer. An exhaustive
  search over the feed confirms nothing better exists in it.
- **Which variant of a train runs today is not published**, so where two
  disagree by a minute one of them is wrong and there is no way to tell which.
  The earlier arrival is taken, because in both cases that could be checked the
  operator published the earlier one.

The planner is not more accurate than its source and does not pretend to be.
Where a minute matters, `get_train` reads the operator's own live data.

### Coverage

Trenord covers its own fleet, FNM included. ViaggiaTreno covers the RFI network
and, unpredictably, part of FNM. So `get_train` asks Trenord first and falls
back to ViaggiaTreno, while the station boards only exist on ViaggiaTreno.

The regional timetable reaches along the cross-border lines, so stations beyond
the frontier are planned domestically rather than as an international journey.
The live APIs stop at the border, and a leg past it therefore carries scheduled
times only.

## Development

```bash
dotnet build
dotnet test
```

Fifty tests, on three levels:

- **the clients**, against the live endpoints — the header rule, the timestamp
  formats, the empty body where an empty array was expected;
- **the tools**, called the way a model calls them, asserting on what the answer
  claims rather than on how it is worded: an ambiguous name comes back as a
  question, an unknown place says where coverage ends, a planned journey says
  its times carry no delays;
- **the server**, started as a process and spoken to in JSON-RPC — the
  handshake, the advertised tools and their schemas, and the rule that nothing
  but protocol may reach stdout.

Nothing asserts a departure time. The timetable is republished daily, and a test
written around today's 08:24 fails next month for no reason, which teaches
whoever reads it to ignore the suite. What is asserted are the properties that
hold whatever the timetable says: time moves forward, a change is long enough to
make, the journey starts and ends where it was asked to, results are ordered the
way the tool claims. Those are also the ones that were actually broken.

They run against the live endpoints on purpose. Mocking would only prove the
mocks match what was assumed, and every bug worth catching here came from the
real payload disagreeing with the assumption — including the two-header rule
above, found by a test contradicting the documentation it was written from.

Requires the .NET 10 SDK.

## Limits

- **Journey planning is Lombardy plus the cross-border lines. Live data is all
  of Italy.** Departure boards, arrivals, direct connections and train tracking
  work at Roma Termini, Napoli Centrale, Palermo and Bari; planning a route
  between them does not, and says so rather than improvising.
- Read-only. No booking, no ticketing, no account access.
- Journeys with more than one change are not searched for.
- Planned times are timetabled times. A train cancelled this morning is still
  in the plan; pair `find_journey` with `get_departures` or `get_train` before
  relying on a tight change.
- ViaggiaTreno is served over plain HTTP and is occasionally unavailable.
- Both APIs are undocumented and can change without notice. If a test starts
  failing, that is the intended alarm.

## Licence

MIT.
