import cv2
import numpy as np

IMAGE_EXTS = ('.jpg', '.jpeg', '.png', '.bmp', '.tif', '.tiff')


def read_gray(path):
    """Read an image as uint8 grayscale; np.fromfile keeps non-ASCII paths working."""
    data = np.fromfile(path, np.uint8)
    if data.size == 0:
        raise IOError('图片文件为空（0 字节），可能是解压或拷贝不完整，请重新获取原图')
    try:
        img = cv2.imdecode(data, cv2.IMREAD_UNCHANGED)
    except cv2.error:
        img = None
    if img is None:
        raise IOError('无法解码图片（文件损坏或不是图片）: %s' % path)
    if img.dtype != np.uint8:
        img = cv2.normalize(img, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
    if img.ndim == 3:
        img = cv2.cvtColor(img, cv2.COLOR_BGRA2GRAY if img.shape[2] == 4 else cv2.COLOR_BGR2GRAY)
    return img


def write_png(path, img):
    ok, buf = cv2.imencode('.png', img)
    if not ok:
        raise IOError('无法编码图片: %s' % path)
    buf.tofile(path)


def clahe(gray, clip=2.0):
    return cv2.createCLAHE(clipLimit=clip, tileGridSize=(8, 8)).apply(cv2.GaussianBlur(gray, (0, 0), 0.8))
