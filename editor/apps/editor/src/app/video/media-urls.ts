import { Injectable, inject } from '@angular/core';
import { mediaUrl } from '@anode/api-client';
import { ConnectionService } from '../core/connection';
import { FAKE_SERVER } from '../core/fake/fake-token';

/** The movie's stream (`GET /project/movie/stream`, HTTP range requests). */
export const MovieStreamPath = '/api/v1/project/movie/stream';

/** A movie frame as a JPEG (`GET /project/movie/frames/{frame}?height=`). */
export function movieFramePath(frame: number): string {
  return `/api/v1/project/movie/frames/${Math.max(0, Math.round(frame))}`;
}

/**
 * The URLs the movie's elements load themselves (`<video src>`, `<img src>`), which cannot send the token as a header
 * (docs/11-phase-10.md, "Contract notes": `mediaUrl`, the token as `access_token`). The desktop app and a browser on
 * the LAN take the same path. The fake Server has no HTTP: its frames are image data URLs and it has no stream (the
 * movie window then shows the frame images).
 */
@Injectable({ providedIn: 'root' })
export class MovieMedia {
  private readonly connection = inject(ConnectionService);
  private readonly fake = inject(FAKE_SERVER, { optional: true });

  /**
   * The movie's stream URL, or null when there is none to play (the fake Server). `version` (the movie's path) is in
   * the query so a new movie is a new URL and the element reloads.
   */
  streamUrl(version: string): string | null {
    const connection = this.connection.connection();
    if (this.fake || !connection) {
      return null;
    }
    return mediaUrl(connection, MovieStreamPath, { v: version });
  }

  /** A frame's thumbnail URL at `height` pixels, or null when there is no Server to ask. */
  frameUrl(frame: number, height: number): string | null {
    if (this.fake) {
      try {
        return this.fake.movieFrameUrl(frame, height);
      } catch {
        return null; // no movie (any more)
      }
    }
    const connection = this.connection.connection();
    return connection ? mediaUrl(connection, movieFramePath(frame), { height }) : null;
  }
}
