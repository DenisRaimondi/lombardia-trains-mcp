# Lombardia Trains MCP

An MCP server that answers questions about Lombardy trains: departure and
arrival boards, live delays, platforms, stop-by-stop progress, cancellations
and crowding. It reads the public ViaggiaTreno (RFI/Trenitalia) and Trenord
APIs. No API key, no account, no scraping.

```
> what time is the next train from Castellanza to Milano?

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
| `search_station` | "what is the station code for Castellanza?" |
| `get_departures` | "what is leaving Milano Cadorna in the next hour?" |
| `get_arrivals` | "when does the train from Varese get in?" |
| `get_train` | "where is train 4307 right now, and how late is it?" |

Station arguments accept either a code (`S01136`) or a name (`castellanza`) —
names are resolved automatically, so the model does not have to chain two calls
to answer a simple question.

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

### Coverage

Trenord covers its own fleet, FNM included. ViaggiaTreno covers the RFI network
and, unpredictably, part of FNM. So `get_train` asks Trenord first and falls
back to ViaggiaTreno, while the station boards only exist on ViaggiaTreno.

## Development

```bash
dotnet build
dotnet test
```

The tests run against the live endpoints on purpose. Mocking them would only
prove that the mocks match what was assumed, and every bug worth catching here
came from the real payload disagreeing with the assumption — including the
two-header rule above, which was found by a test contradicting the
documentation it was written from.

Requires the .NET 10 SDK.

## Limits

- Read-only. No booking, no ticketing, no account access.
- ViaggiaTreno is served over plain HTTP and is occasionally unavailable.
- Both APIs are undocumented and can change without notice. If a test starts
  failing, that is the intended alarm.

## Licence

MIT.
