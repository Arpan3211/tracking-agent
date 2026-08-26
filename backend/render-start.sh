#!/bin/sh
# Render-only startup wrapper (see render.yaml's dockerCommand comment) -
# Render's dockerCommand does a plain whitespace split with no shell
# interpretation, so a one-line "a && b && c" string doesn't work: each
# word becomes a literal argv element (e.g. alembic receives "&&" as one of
# its own CLI arguments). Invoking `sh render-start.sh` instead gives
# Render only two space-free tokens, and *this* file is what a real shell
# parses - so && works normally inside it.
set -e
alembic upgrade head
python -m scripts.seed_admin
exec uvicorn app.main:app --host 0.0.0.0 --port 8000
