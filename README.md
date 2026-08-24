# Lombardia Trains MCP

<!-- mcp-name: io.github.denisraimondi/lombardia-trains -->

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

### Read the operator's file, not the portal's copy of it

Trenord publishes this timetable as a GTFS zip under CC0, and the same portal
also serves it exploded into one queryable table per file. The tables look like
the easier option — paged JSON, filterable, no zip to unpack — and they are a
lossy copy. Measured against the file they are built from:

| | the zip | the tables |
|---|---|---|
| stop times | 90,553 | 68,952 |
| trips | 8,470 | 6,265 |

A quarter of the timetable does not survive the import, and it does not go
missing tidily: trips arrive **truncated**. Train 11827 runs Varese to Milano
and on; in the tables it stops at Porta Garibaldi, so everything reachable by
staying on it is invisible, and Varese to Bergamo came back an hour worse than
the published answer. Reading the zip, it matches to the minute.

Three more things the import breaks, each of which costs real code to work
around and none of which is wrong in the file:

- **Times land inside a placeholder date.** GTFS writes a service past midnight
  as `24:05`; a datetime cannot hold that, so it becomes `00:05` and the train
  arrives eighteen hours before it left. The file says `24:05`.
- **Service ids are rewritten with a hash** and no longer match the ones in
  `calendar_dates`, so the two files cannot be joined on the key they share.
  Cutting both back to the number before the hyphen makes them join again — and
  merges every seasonal variant of a train into one, leaving nothing to say
  which of them runs today. In the file, `trips` and `calendar_dates` both write
  `1001A-2025-12-14-2026-12-12`. The join is exact and the ambiguity does not
  exist.
- **`trip_short_name` is dropped entirely.** That is the train number — the one
  field that connects a planned leg to the live data.

The decimal point also goes missing from coordinates, and `route_type` differs:
the tables call R23 and RE4 trains, the file calls them buses.

### Ask the operator, then say so

The file is not the operator's own answer either, and it is worth being precise
about the size of the gap. Checked against 28 journeys published by the
operator's own planner, on the same day:

- Milano Centrale to Bergamo: the feed times train 2217 at 48 minutes, the
  operator at 52.
- Pavia to Mortara: train 10668 arrives 09:28 in the feed, 09:23 in the answer.
- Lecco to Bergamo: train 10719 reaches Ponte S.Pietro at 08:52 in the feed and
  its connecting coach leaves at 08:51 — a change the feed itself makes
  impossible and the operator has working with five minutes to spare.

The operator runs HAFAS over an internal timetable with real-time folded in. A
GTFS export of it is not it. But the live sources here are the operator's own,
and the file carries the train number, so every leg can simply be looked up and
asked. Where it answers, its times are the ones shown, the timetable's are kept
in brackets beside them, and platforms, delays and cancellations come with them.

Against those same 28 journeys that takes exact agreement from 22 to **27**.

The one that remains is the shape of what this cannot do. Lecco to Bergamo is
still answered with the 09:01 coach rather than the 08:51 one, because the feed
said the train arrived at 08:52 and the search discarded that connection before
anything was checked. Correcting after the planning fixes what is shown; it
does not recover what the wrong data excluded. Live data is asked for today and
tomorrow only — beyond that there is nothing live to ask.

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

### How accurate this is, and how that was measured

Twenty-eight journeys published by the operator's own planner were compared
against what this returns — same day, same times, thirteen routes across the
region — and where the two disagreed, the live train data was asked to settle
it. **Twenty-seven of the twenty-eight match to the minute.**

That is a measurement of two things at once, and they are worth separating. The
routing was never really the hard part on a network this size: what the number
mostly measures is how faithfully open timetable data reproduces the timetable
the operator actually runs. The answer is: closely, and not exactly.

An open feed is an export. The operator plans on an internal system with
real-time folded into it and publishes a snapshot of that system as GTFS, on its
own schedule. A snapshot trails the thing it is a snapshot of — that is what a
snapshot is, not a defect of anyone's — and the trailing shows up as a few
minutes on a few trains. Four into Bergamo. Five into Mortara. Six into Ponte
S.Pietro, where it is the difference between a coach that can be caught and one
that cannot.

None of that can be repaired from open timetable data, because the correct value
is not in it. What can be done is to stop treating the timetable as the last
word: every leg carries a train number, the live sources belong to the operator,
so each leg is looked up and asked. That is where twenty-two of twenty-eight
became twenty-seven.

**The twenty-eighth is the honest edge of the approach.** Lecco to Bergamo is
still answered with the 09:01 coach rather than the 08:51 one. The feed puts the
inbound train into Ponte S.Pietro at 08:52, one minute after that coach leaves,
so the search discarded the connection before anything was checked against the
operator. Correcting after planning fixes what is shown; it cannot recover what
wrong data excluded. Doing better would mean planning on corrected times, which
means correcting the whole timetable rather than the handful of legs an answer
happens to use — a different project, and a much larger one.

So the position this takes is: be exact about what is known, name the source of
every number, and where a minute matters, go and ask the operator. A tool that
knows which of its answers to distrust is more useful than one that is confident
everywhere.

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

- **Journey planning covers Lombardy and the cross-border lines. Live data
  covers all of Italy.** Departure boards, arrivals, direct connections and
  train tracking work at Roma Termini, Napoli Centrale, Palermo and Bari;
  planning a route between them does not, and says so rather than improvising.
- **One change.** Two multiply both the search space and the ways to be quietly
  wrong. Where a journey needs more, the answer says which limit was hit rather
  than reporting nothing found.
- **Times are the operator's where it was asked, and timetabled where it was
  not.** The live sources are asked for today and tomorrow; for any other day
  there is nothing live to ask, and the answer says so.
- **A few minutes on a few trains.** The published timetable trails the
  operator's own by four to six minutes on some services. Legs where the two
  disagree are marked, and are the legs not to build a four-minute change on.
- **Walking between stations is not modelled.** Several towns have two stations
  a few hundred metres apart, served by different lines; a journey that would
  change between them on foot is not found.
- Read-only. No booking, no ticketing, no account access.
- ViaggiaTreno is served over plain HTTP and is occasionally unavailable.
- The live APIs are undocumented and can change without notice. The tests run
  against them on purpose: if one starts failing, that is the intended alarm.

## Licence

MIT.
