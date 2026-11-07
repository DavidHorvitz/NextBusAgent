"""Builds BusPulse.Tests/Fixtures/gtfs-mini.zip from the real Ministry of Transport GTFS feed (stdlib only).

Usage (from the repo root; the feed is the ~150 MB israel-public-transportation.zip downloaded by hand):

    python -I tools/make_gtfs_fixture.py <path-to-feed.zip> BusPulse.Tests/Fixtures/gtfs-mini.zip

The feed snapshot used was the one published on 2026-11-07 (valid 2026-11-07 .. 2026-12-07). The script keeps:
  * three real trips that call at stop code 3490 on Sunday 2026-11-08 (two sharing one service, one with its own),
  * one real trip of Sunday 2026-11-08 (its own service) whose calls run past midnight (times of 24:00:00 and later),
  * only the calls at stop 3490, the two stops after it on the first trip, and the stop of the first after-midnight call,
  * the matching agency, routes, stops and calendar rows, copied verbatim (csv quoting kept).
The real feed has no calendar_dates.txt, so that file is the one synthetic part: it removes the second service on
2026-11-08 and adds the first service on Sunday 2026-11-15, to exercise both exception types.
"""
import csv
import io
import sys
import zipfile

STOP_CODE = '3490'
SERVICE_DAY = '081126'      # trip_id suffix of Sunday 2026-11-08
FIXED_TIME = (2026, 11, 7, 0, 0, 0)


def read_csv(zf, name):
    with zf.open(name) as handle:
        return list(csv.DictReader(io.TextIOWrapper(handle, 'utf-8-sig', newline='')))


def seconds(text):
    h, m, s = text.split(':')
    return int(h) * 3600 + int(m) * 60 + int(s)


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    feed_path, out_path = sys.argv[1], sys.argv[2]
    feed = zipfile.ZipFile(feed_path)
    calendar = {row['service_id']: row for row in read_csv(feed, 'calendar.txt')}
    stops = read_csv(feed, 'stops.txt')
    stop_by_id = {row['stop_id']: row for row in stops}
    target_ids = {row['stop_id'] for row in stops if row['stop_code'] == STOP_CODE}
    trips = {row['trip_id']: row for row in read_csv(feed, 'trips.txt')
             if row['trip_id'].endswith('_' + SERVICE_DAY)}

    def only_that_day(trip, day):
        service = calendar[trip['service_id']]
        return service['start_date'] == service['end_date'] == day

    # Pass over stop_times: calls of the target stop, and after-midnight calls of the Sunday trips.
    at_target = []   # (arrival seconds, trip_id)
    late = {}        # trip_id -> first call row at or past 24:00:00
    with feed.open('stop_times.txt') as handle:
        reader = csv.DictReader(io.TextIOWrapper(handle, 'utf-8-sig', newline=''))
        for row in reader:
            trip = trips.get(row['trip_id'])
            if trip is None:
                continue
            if row['stop_id'] in target_ids and row['trip_id'].endswith('_' + SERVICE_DAY) and only_that_day(trip, '20261108'):
                at_target.append((seconds(row['arrival_time']), row['trip_id']))
            if (only_that_day(trip, '20261108')
                    and seconds(row['arrival_time']) >= 86400 and row['trip_id'] not in late):
                late[row['trip_id']] = row

    at_target.sort()
    morning = [item for item in at_target if item[0] >= 7 * 3600]
    first = morning[0][1]
    second = morning[1][1]
    other = next(trip_id for _, trip_id in morning
                 if trips[trip_id]['service_id'] != trips[first]['service_id'])
    # A service shared by the first two, a different one for the third.
    if trips[second]['service_id'] != trips[first]['service_id']:
        second = next(trip_id for _, trip_id in morning[1:]
                      if trips[trip_id]['service_id'] == trips[first]['service_id'])
    taken = {trips[first]['service_id'], trips[other]['service_id']}
    late_trip = next(t for t in sorted(late) if trips[t]['service_id'] not in taken)
    chosen = [first, second, other, late_trip]
    chosen_set = set(chosen)

    # Second pass: all calls of the chosen trips (to pick the neighbouring stops), then the filtered rows.
    calls = {trip_id: [] for trip_id in chosen}
    with feed.open('stop_times.txt') as handle:
        for row in csv.DictReader(io.TextIOWrapper(handle, 'utf-8-sig', newline='')):
            if row['trip_id'] in chosen_set:
                calls[row['trip_id']].append(row)
    first_calls = sorted(calls[first], key=lambda r: int(r['stop_sequence']))
    position = next(i for i, r in enumerate(first_calls) if r['stop_id'] in target_ids)
    keep_stop_ids = {r['stop_id'] for r in first_calls[position:position + 3]}
    keep_stop_ids.add(late[late_trip]['stop_id'])

    kept_rows = []
    for trip_id in chosen:
        kept_rows.extend(r for r in calls[trip_id] if r['stop_id'] in keep_stop_ids)

    kept_trips = [trips[t] for t in chosen]
    route_ids = {t['route_id'] for t in kept_trips}
    routes = [r for r in read_csv(feed, 'routes.txt') if r['route_id'] in route_ids]
    agency_ids = {r['agency_id'] for r in routes}
    agencies = [r for r in read_csv(feed, 'agency.txt') if r['agency_id'] in agency_ids]
    services = sorted({t['service_id'] for t in kept_trips}, key=int)
    kept_stops = [stop_by_id[s] for s in sorted(keep_stop_ids, key=int)]

    first_service = trips[first]['service_id']
    other_service = trips[other]['service_id']
    calendar_dates = [
        {'service_id': other_service, 'date': '20261108', 'exception_type': '2'},
        {'service_id': first_service, 'date': '20261115', 'exception_type': '1'},
    ]

    def table(rows, fields):
        buffer = io.StringIO(newline='')
        writer = csv.DictWriter(buffer, fieldnames=fields, lineterminator='\r\n', extrasaction='ignore')
        writer.writeheader()
        writer.writerows(rows)
        return buffer.getvalue().encode('utf-8')

    files = {
        'agency.txt': table(agencies, list(agencies[0].keys())),
        'routes.txt': table(routes, list(routes[0].keys())),
        'trips.txt': table(kept_trips, list(kept_trips[0].keys())),
        'stops.txt': table(kept_stops, list(kept_stops[0].keys())),
        'stop_times.txt': table(kept_rows, list(kept_rows[0].keys())),
        'calendar.txt': table([calendar[s] for s in services], list(calendar[services[0]].keys())),
        'calendar_dates.txt': table(calendar_dates, ['service_id', 'date', 'exception_type']),
    }
    with zipfile.ZipFile(out_path, 'w', zipfile.ZIP_DEFLATED) as out:
        for name, data in files.items():
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            out.writestr(info, data)

    print('trips', chosen)
    print('services', {t: trips[t]['service_id'] for t in chosen})
    print('stops', [(s['stop_id'], s['stop_code'], s['stop_name']) for s in kept_stops])
    print('late call', late[late_trip]['trip_id'], late[late_trip]['arrival_time'], stop_by_id[late[late_trip]['stop_id']]['stop_code'])
    for row in kept_rows:
        print(row['trip_id'], row['stop_sequence'], row['arrival_time'], row['departure_time'], stop_by_id[row['stop_id']]['stop_code'])
    print('routes', [(r['route_id'], r['route_short_name'], r['route_long_name']) for r in routes])
    print('agencies', [a['agency_name'] for a in agencies])


if __name__ == '__main__':
    main()
