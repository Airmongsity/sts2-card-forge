"""SQLite persistence for cards, generated images and the job queue."""
import json
import sqlite3
import threading
import time

import config

SCHEMA = """
CREATE TABLE IF NOT EXISTS cards (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project TEXT NOT NULL DEFAULT 'default',
    name TEXT NOT NULL DEFAULT '',
    slug TEXT NOT NULL DEFAULT '',
    cls TEXT NOT NULL DEFAULT 'colorless',
    type TEXT NOT NULL DEFAULT 'attack',
    rarity TEXT NOT NULL DEFAULT 'common',
    cost TEXT NOT NULL DEFAULT '1',
    description TEXT NOT NULL DEFAULT '',
    concept TEXT NOT NULL DEFAULT '',
    prompt TEXT NOT NULL DEFAULT '',
    negative TEXT NOT NULL DEFAULT '',
    params TEXT NOT NULL DEFAULT '{}',
    selected_image INTEGER,
    created REAL, updated REAL
);
CREATE TABLE IF NOT EXISTS images (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    card_id INTEGER,
    path TEXT NOT NULL,
    width INTEGER, height INTEGER,
    seed INTEGER,
    prompt TEXT,
    params TEXT NOT NULL DEFAULT '{}',
    favorite INTEGER NOT NULL DEFAULT 0,
    created REAL
);
CREATE TABLE IF NOT EXISTS jobs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    card_id INTEGER,
    status TEXT NOT NULL,
    params TEXT NOT NULL,
    progress REAL NOT NULL DEFAULT 0,
    message TEXT NOT NULL DEFAULT '',
    image_id INTEGER,
    created REAL, started REAL, finished REAL
);
CREATE TABLE IF NOT EXISTS characters (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL DEFAULT '',
    base_class TEXT NOT NULL DEFAULT 'colorless',
    color TEXT NOT NULL DEFAULT '#7a7a9a',
    palette TEXT NOT NULL DEFAULT '',
    appearance TEXT NOT NULL DEFAULT '',
    ref_image TEXT NOT NULL DEFAULT '',
    use_ref INTEGER NOT NULL DEFAULT 1,
    created REAL
);
CREATE INDEX IF NOT EXISTS images_card ON images(card_id);
CREATE INDEX IF NOT EXISTS jobs_status ON jobs(status);
"""

CARD_FIELDS = ("project", "name", "slug", "cls", "type", "rarity", "cost", "description", "concept", "prompt",
               "negative", "params", "selected_image")

_db = sqlite3.connect(config.DB_FILE, check_same_thread=False)
_db.row_factory = sqlite3.Row
_db.executescript(SCHEMA)
_lock = threading.RLock()


def _q(sql, args=()):
    with _lock:
        cur = _db.execute(sql, args)
        _db.commit()
        return cur


def _row(r, json_fields=("params",)):
    if r is None:
        return None
    d = dict(r)
    for f in json_fields:
        if f in d and isinstance(d[f], str):
            try:
                d[f] = json.loads(d[f])
            except ValueError:
                d[f] = {}
    return d


# ---- cards -----------------------------------------------------------------------------------

def list_cards(project=None):
    sql = """SELECT c.*, (SELECT COUNT(*) FROM images i WHERE i.card_id = c.id) AS image_count,
                    (SELECT created FROM images i WHERE i.id = c.selected_image) AS selected_created,
                    (SELECT COUNT(*) FROM jobs j WHERE j.card_id = c.id AND j.status IN ('queued','running')) AS pending
             FROM cards c"""
    rows = _q(sql + (" WHERE project = ? ORDER BY id" if project else " ORDER BY project, id"),
              (project,) if project else ()).fetchall()
    return [_row(r) for r in rows]


def get_card(cid):
    return _row(_q("""SELECT c.*, (SELECT created FROM images i WHERE i.id = c.selected_image) AS selected_created
                      FROM cards c WHERE c.id = ?""", (cid,)).fetchone())


def create_card(data):
    now = time.time()
    fields = {k: data[k] for k in CARD_FIELDS if k in data}
    if "params" in fields:
        fields["params"] = json.dumps(fields["params"] or {}, ensure_ascii=False)
    fields["created"] = fields["updated"] = now
    cols = ", ".join(fields)
    cur = _q(f"INSERT INTO cards ({cols}) VALUES ({', '.join('?' * len(fields))})", tuple(fields.values()))
    return get_card(cur.lastrowid)


def update_card(cid, data):
    fields = {k: data[k] for k in CARD_FIELDS if k in data}
    if not fields:
        return get_card(cid)
    if "params" in fields:
        fields["params"] = json.dumps(fields["params"] or {}, ensure_ascii=False)
    fields["updated"] = time.time()
    _q(f"UPDATE cards SET {', '.join(k + ' = ?' for k in fields)} WHERE id = ?", (*fields.values(), cid))
    return get_card(cid)


def delete_card(cid):
    """Deletes the card; its images stay in the gallery as loose images."""
    _q("UPDATE images SET card_id = NULL WHERE card_id = ?", (cid,))
    _q("UPDATE jobs SET status = 'cancelled' WHERE card_id = ? AND status = 'queued'", (cid,))
    _q("DELETE FROM cards WHERE id = ?", (cid,))


def projects():
    return [r[0] for r in _q("SELECT DISTINCT project FROM cards ORDER BY project").fetchall()]


# ---- images ----------------------------------------------------------------------------------

def add_image(card_id, path, width, height, seed, prompt, params, auto_select=True):
    cur = _q("INSERT INTO images (card_id, path, width, height, seed, prompt, params, created) VALUES (?,?,?,?,?,?,?,?)",
             (card_id, str(path), width, height, seed, prompt, json.dumps(params, ensure_ascii=False), time.time()))
    iid = cur.lastrowid
    if card_id is not None and auto_select:
        _q("UPDATE cards SET selected_image = ? WHERE id = ? AND selected_image IS NULL", (iid, card_id))
    return get_image(iid)


def get_image(iid):
    return _row(_q("SELECT * FROM images WHERE id = ?", (iid,)).fetchone())


def list_images(card_id=None, project=None, favorites=False, limit=500, offset=0):
    sql = "SELECT i.*, c.name AS card_name, c.project AS project FROM images i LEFT JOIN cards c ON c.id = i.card_id"
    where, args = [], []
    if card_id is not None:
        where.append("i.card_id = ?")
        args.append(card_id)
    if project:
        where.append("c.project = ?")
        args.append(project)
    if favorites:
        where.append("i.favorite = 1")
    if where:
        sql += " WHERE " + " AND ".join(where)
    sql += " ORDER BY i.id DESC LIMIT ? OFFSET ?"
    return [_row(r) for r in _q(sql, (*args, limit, offset)).fetchall()]


def set_favorite(iid, fav):
    _q("UPDATE images SET favorite = ? WHERE id = ?", (1 if fav else 0, iid))


def delete_image(iid):
    img = get_image(iid)
    _q("UPDATE cards SET selected_image = NULL WHERE selected_image = ?", (iid,))
    _q("DELETE FROM images WHERE id = ?", (iid,))
    return img


# ---- jobs ------------------------------------------------------------------------------------

def add_job(card_id, params):
    cur = _q("INSERT INTO jobs (card_id, status, params, created) VALUES (?, 'queued', ?, ?)",
             (card_id, json.dumps(params, ensure_ascii=False), time.time()))
    return cur.lastrowid


def get_job(jid):
    return _row(_q("SELECT * FROM jobs WHERE id = ?", (jid,)).fetchone())


def next_job():
    return _row(_q("SELECT * FROM jobs WHERE status = 'queued' ORDER BY id LIMIT 1").fetchone())


def update_job(jid, **kw):
    if kw:
        _q(f"UPDATE jobs SET {', '.join(k + ' = ?' for k in kw)} WHERE id = ?", (*kw.values(), jid))


def list_jobs(active_only=False, limit=200):
    sql = ("SELECT j.*, c.name AS card_name, i.created AS image_created FROM jobs j "
           "LEFT JOIN cards c ON c.id = j.card_id LEFT JOIN images i ON i.id = j.image_id")
    if active_only:
        sql += " WHERE j.status IN ('queued', 'running')"
    sql += " ORDER BY CASE j.status WHEN 'running' THEN 0 WHEN 'queued' THEN 1 ELSE 2 END, j.id DESC LIMIT ?"
    return [_row(r) for r in _q(sql, (limit,)).fetchall()]


def count_queued():
    return _q("SELECT COUNT(*) FROM jobs WHERE status = 'queued'").fetchone()[0]


def clear_finished():
    _q("DELETE FROM jobs WHERE status IN ('done', 'failed', 'cancelled')")


def recover_after_restart():
    """A job that was running when the backend died did not finish; put it back in the queue."""
    _q("UPDATE jobs SET status = 'queued', progress = 0, message = '' WHERE status = 'running'")


# ---- MOD characters (custom classes) --------------------------------------------------------

CHARACTER_FIELDS = ("name", "color", "palette", "appearance", "ref_image", "use_ref")


def list_characters():
    return [dict(r) for r in _q("SELECT * FROM characters ORDER BY created").fetchall()]


def get_character(cid):
    r = _q("SELECT * FROM characters WHERE id = ?", (cid,)).fetchone()
    return dict(r) if r else None


def save_character(cid, data):
    fields = {k: data[k] for k in CHARACTER_FIELDS if k in data}
    if "use_ref" in fields:
        fields["use_ref"] = 1 if fields["use_ref"] else 0
    if get_character(cid):
        if fields:
            _q(f"UPDATE characters SET {', '.join(k + ' = ?' for k in fields)} WHERE id = ?", (*fields.values(), cid))
    else:
        fields.setdefault("use_ref", 1)     # cards follow the character's reference image by default
        fields.update(id=cid, created=time.time())
        _q(f"INSERT INTO characters ({', '.join(fields)}) VALUES ({', '.join('?' * len(fields))})", tuple(fields.values()))
    return get_character(cid)


def delete_character(cid):
    _q("DELETE FROM characters WHERE id = ?", (cid,))
