import uuid
from dataclasses import asdict, dataclass, field

from .config import OTHER


@dataclass
class Defect:
    x0: int
    y0: int
    x1: int
    y1: int
    type: str = OTHER
    score: float = 0.0
    source: str = 'auto'      # auto | manual
    status: str = 'pending'   # pending | accepted | rejected
    by: str = ''              # e.g. 单元3·线1
    id: str = field(default_factory=lambda: uuid.uuid4().hex[:8])

    def __post_init__(self):
        self.x0, self.y0, self.x1, self.y1 = (int(v) for v in (self.x0, self.y0, self.x1, self.y1))
        self.score = float(self.score)

    def to_dict(self):
        return asdict(self)

    @staticmethod
    def from_dict(d):
        return Defect(**{k: d[k] for k in Defect.__dataclass_fields__ if k in d})


def iou(a, b):
    ix = max(0, min(a['x1'], b['x1']) - max(a['x0'], b['x0']))
    iy = max(0, min(a['y1'], b['y1']) - max(a['y0'], b['y0']))
    inter = ix * iy
    union = (a['x1'] - a['x0']) * (a['y1'] - a['y0']) + (b['x1'] - b['x0']) * (b['y1'] - b['y0']) - inter
    return inter / max(union, 1)


def overlaps(a, b):
    cx, cy = (a['x0'] + a['x1']) / 2, (a['y0'] + a['y1']) / 2
    return iou(a, b) >= 0.3 or (b['x0'] <= cx <= b['x1'] and b['y0'] <= cy <= b['y1'])
