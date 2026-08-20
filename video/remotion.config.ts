import { Config } from "@remotion/cli/config";

/**
 * PNG, not JPEG, for the intermediate frames.
 *
 * Remotion renders each frame to an image and then encodes those images. The default is JPEG at
 * quality 80, which is fine for photographic video and wrong for this: a screen recording is text and
 * hairlines on flat colour, exactly what JPEG's chroma subsampling and block transform ruin. Every
 * frame was being degraded once before the encoder even saw it.
 */
Config.setVideoImageFormat("png");
Config.setOverwriteOutput(true);
Config.setChromiumOpenGlRenderer("angle");

/**
 * CRF 17. The default (18 for h264) is tuned for camera footage; screen text at 60fps needs more bits
 * during motion or scrolling smears. 17 roughly doubles the file and is the difference between
 * "readable while it scrolls" and "readable when it stops".
 */
Config.setCrf(17);
