"""structured event details

Revision ID: ae3336a9cda1
Revises: ae41ff2bc6af
Create Date: 2026-08-25 00:00:00.000000

The agent now sends a structured JSON payload per event (built per event
type on the agent itself) instead of a "key=value; key2=value2" string that
the backend used to regex-parse afterward. That collapses the old two-column
split (details_raw verbatim string, details_parsed best-effort parse) into
one JSONB column that holds exactly what the agent sent, no parsing step.
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = 'ae3336a9cda1'
down_revision: Union[str, None] = 'ae41ff2bc6af'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.drop_column('activity_events', 'details_raw')
    op.alter_column('activity_events', 'details_parsed', new_column_name='details')


def downgrade() -> None:
    op.alter_column('activity_events', 'details', new_column_name='details_parsed')
    op.add_column('activity_events', sa.Column('details_raw', sa.Text(), nullable=True))
