import { GLYPH_ADVANCE, type RectBatch } from '../frame-builder';
import { RendererUnavailableError, type RenderTarget, type TimelineRenderer } from './renderer';

/** The font both renderers draw text in; its advance is {@link GLYPH_ADVANCE}. */
export const TEXT_FONT = '10px ui-monospace, Menlo, Consolas, monospace';

/** Glyphs in the atlas (ASCII 32–126). */
const GLYPH_COUNT = 95;

const RECT_VERTEX = `#version 300 es
layout(location = 0) in vec2 a_corner;
layout(location = 1) in vec4 a_rect;
layout(location = 2) in vec4 a_color;
layout(location = 3) in float a_style;
uniform vec2 u_size;
out vec4 v_color;
out vec2 v_local;
out vec2 v_extent;
flat out int v_style;
void main() {
  vec2 p = a_rect.xy + a_corner * a_rect.zw;
  gl_Position = vec4(p.x / u_size.x * 2.0 - 1.0, 1.0 - p.y / u_size.y * 2.0, 0.0, 1.0);
  v_color = a_color;
  v_local = a_corner * a_rect.zw;
  v_extent = a_rect.zw;
  v_style = int(a_style);
}`;

// Notes and regions get a 1 px darker border; regions also a lighter name bar. Shading in the fragment shader keeps it
// one instance per item.
const RECT_FRAGMENT = `#version 300 es
precision highp float;
in vec4 v_color;
in vec2 v_local;
in vec2 v_extent;
flat in int v_style;
out vec4 o_color;
void main() {
  vec4 c = v_color;
  if (v_style > 0) {
    vec2 edge = min(v_local, v_extent - v_local);
    if (min(edge.x, edge.y) < 1.0) {
      c.rgb *= 0.55;
    } else if (v_style == 2 && v_local.y < 12.0) {
      c.rgb = mix(c.rgb, vec3(1.0), 0.3);
    }
  }
  o_color = c;
}`;

const GLYPH_VERTEX = `#version 300 es
layout(location = 0) in vec2 a_corner;
layout(location = 1) in vec3 a_glyph;
layout(location = 2) in vec4 a_color;
uniform vec2 u_size;
uniform vec2 u_cell;
out vec2 v_uv;
out vec4 v_color;
void main() {
  vec2 p = a_glyph.xy + a_corner * u_cell;
  gl_Position = vec4(p.x / u_size.x * 2.0 - 1.0, 1.0 - p.y / u_size.y * 2.0, 0.0, 1.0);
  v_uv = vec2((a_glyph.z + a_corner.x) / ${GLYPH_COUNT}.0, a_corner.y);
  v_color = a_color;
}`;

const GLYPH_FRAGMENT = `#version 300 es
precision mediump float;
uniform sampler2D u_atlas;
in vec2 v_uv;
in vec4 v_color;
out vec4 o_color;
void main() {
  o_color = vec4(v_color.rgb, v_color.a * texture(u_atlas, v_uv).a);
}`;

/** Glyph cell in the atlas, CSS pixels: printable ASCII in 10 px monospace. */
const CELL_WIDTH = GLYPH_ADVANCE;
const CELL_HEIGHT = 12;
/** The atlas holds ASCII 32 (space) to 126 (~); anything else draws as `?`. */
const FIRST_GLYPH = 32;
const LAST_GLYPH = 126;

/**
 * Instanced-quad renderer: one unit quad, one instance per rectangle (position and size as `vec4`, colour as normalised
 * bytes, style as a byte), so a frame is three buffer uploads and one draw call, plus one for bar-number glyphs from a
 * digit atlas.
 */
export class WebGL2Renderer implements TimelineRenderer {
  readonly backend = 'webgl2' as const;
  readonly description: string;
  private readonly gl: WebGL2RenderingContext;
  private readonly rectProgram: WebGLProgram;
  private readonly glyphProgram: WebGLProgram;
  private readonly rectVao: WebGLVertexArrayObject;
  private readonly glyphVao: WebGLVertexArrayObject;
  private readonly rectBuffer: WebGLBuffer;
  private readonly colorBuffer: WebGLBuffer;
  private readonly styleBuffer: WebGLBuffer;
  private readonly glyphBuffer: WebGLBuffer;
  private readonly atlas: WebGLTexture;
  private readonly uniforms: {
    rectSize: WebGLUniformLocation | null;
    glyphSize: WebGLUniformLocation | null;
    glyphCell: WebGLUniformLocation | null;
  };
  private glyphs = new Float32Array(3 * 256);
  private glyphColors = new Uint32Array(256);
  private readonly glyphColorBuffer: WebGLBuffer;
  private readonly pixel = new Uint8Array(4);
  private width = 1;
  private height = 1;
  private dpr = 1;

  constructor(private readonly canvas: RenderTarget) {
    const gl = canvas.getContext('webgl2', {
      alpha: false,
      antialias: false,
      depth: false,
      stencil: false,
      premultipliedAlpha: false,
      powerPreference: 'high-performance'
    });
    if (!gl) {
      throw new RendererUnavailableError('webgl2', 'This canvas has no WebGL2 context.');
    }
    this.gl = gl;
    const info = gl.getExtension('WEBGL_debug_renderer_info');
    this.description = String(gl.getParameter(info ? info.UNMASKED_RENDERER_WEBGL : gl.RENDERER));

    this.rectProgram = program(gl, RECT_VERTEX, RECT_FRAGMENT);
    this.glyphProgram = program(gl, GLYPH_VERTEX, GLYPH_FRAGMENT);
    this.uniforms = {
      rectSize: gl.getUniformLocation(this.rectProgram, 'u_size'),
      glyphSize: gl.getUniformLocation(this.glyphProgram, 'u_size'),
      glyphCell: gl.getUniformLocation(this.glyphProgram, 'u_cell')
    };

    const corners = buffer(gl);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 1, 0, 0, 1, 1, 1]), gl.STATIC_DRAW);

    this.rectVao = vertexArray(gl);
    bindCorners(gl, corners);
    this.rectBuffer = buffer(gl);
    instanced(gl, 1, 4, gl.FLOAT, false);
    this.colorBuffer = buffer(gl);
    instanced(gl, 2, 4, gl.UNSIGNED_BYTE, true);
    this.styleBuffer = buffer(gl);
    instanced(gl, 3, 1, gl.UNSIGNED_BYTE, false);

    this.glyphVao = vertexArray(gl);
    bindCorners(gl, corners);
    this.glyphBuffer = buffer(gl);
    instanced(gl, 1, 3, gl.FLOAT, false);
    this.glyphColorBuffer = buffer(gl);
    instanced(gl, 2, 4, gl.UNSIGNED_BYTE, true);
    gl.bindVertexArray(null);

    const atlas = gl.createTexture();
    this.atlas = atlas;
    gl.disable(gl.DEPTH_TEST);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
  }

  resize(width: number, height: number, dpr: number): void {
    this.width = width;
    this.height = height;
    const scaleChanged = dpr !== this.dpr;
    this.dpr = dpr;
    this.canvas.width = Math.round(width * dpr);
    this.canvas.height = Math.round(height * dpr);
    this.gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    if (scaleChanged || !this.atlasReady) {
      this.buildAtlas();
    }
  }

  draw(batch: RectBatch): void {
    const { gl, uniforms } = this;
    gl.useProgram(this.rectProgram);
    gl.uniform2f(uniforms.rectSize, this.width, this.height);
    gl.bindVertexArray(this.rectVao);
    // bufferData (not bufferSubData) orphans last frame's storage, so the upload never waits for the GPU to finish it.
    gl.bindBuffer(gl.ARRAY_BUFFER, this.rectBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, batch.rects, gl.STREAM_DRAW, 0, 4 * batch.count);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.colorBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Uint8Array(batch.colors.buffer, 0, 4 * batch.count), gl.STREAM_DRAW);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.styleBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, batch.styles, gl.STREAM_DRAW, 0, batch.count);
    gl.disable(gl.BLEND);
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, batch.count);

    const glyphCount = this.layoutGlyphs(batch);
    if (glyphCount > 0) {
      gl.useProgram(this.glyphProgram);
      gl.uniform2f(uniforms.glyphSize, this.width, this.height);
      gl.uniform2f(uniforms.glyphCell, CELL_WIDTH, CELL_HEIGHT);
      gl.bindVertexArray(this.glyphVao);
      gl.bindBuffer(gl.ARRAY_BUFFER, this.glyphBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, this.glyphs, gl.STREAM_DRAW, 0, 3 * glyphCount);
      gl.bindBuffer(gl.ARRAY_BUFFER, this.glyphColorBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, new Uint8Array(this.glyphColors.buffer, 0, 4 * glyphCount), gl.STREAM_DRAW);
      gl.bindTexture(gl.TEXTURE_2D, this.atlas);
      gl.enable(gl.BLEND);
      gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, glyphCount);
    }
    gl.bindVertexArray(null);
  }

  finish(): void {
    this.gl.readPixels(0, 0, 1, 1, this.gl.RGBA, this.gl.UNSIGNED_BYTE, this.pixel);
  }

  dispose(): void {
    this.gl.getExtension('WEBGL_lose_context')?.loseContext();
  }

  private atlasReady = false;

  /** Rasterises printable ASCII with Canvas2D (available in workers through OffscreenCanvas) into a texture. */
  private buildAtlas(): void {
    const { gl, dpr } = this;
    const canvas = new OffscreenCanvas(Math.ceil(GLYPH_COUNT * CELL_WIDTH * dpr), Math.ceil(CELL_HEIGHT * dpr));
    const context = canvas.getContext('2d');
    if (!context) {
      return;
    }
    context.scale(dpr, dpr);
    context.fillStyle = '#fff';
    context.font = TEXT_FONT;
    context.textBaseline = 'top';
    context.textAlign = 'center';
    for (let glyph = 0; glyph < GLYPH_COUNT; glyph++) {
      context.fillText(String.fromCharCode(FIRST_GLYPH + glyph), (glyph + 0.5) * CELL_WIDTH, 1);
    }
    gl.bindTexture(gl.TEXTURE_2D, this.atlas);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, canvas);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    this.atlasReady = true;
  }

  /** One glyph instance (x, y, glyph) and its colour per character of every text. */
  private layoutGlyphs(batch: RectBatch): number {
    let n = 0;
    for (let i = 0; i < batch.textCount; i++) {
      const text = batch.texts[i] ?? '';
      if (this.glyphColors.length < n + text.length) {
        const capacity = 2 * (n + text.length);
        const glyphs = new Float32Array(3 * capacity);
        glyphs.set(this.glyphs);
        this.glyphs = glyphs;
        const colors = new Uint32Array(capacity);
        colors.set(this.glyphColors);
        this.glyphColors = colors;
      }
      const x = batch.textX[i] ?? 0;
      const y = batch.textY[i] ?? 0;
      const color = batch.textColor[i] ?? 0xffffffff;
      for (let c = 0; c < text.length; c++) {
        const code = text.charCodeAt(c);
        if (code === 32) {
          continue;
        }
        this.glyphs[3 * n] = x + c * CELL_WIDTH;
        this.glyphs[3 * n + 1] = y;
        this.glyphs[3 * n + 2] = (code >= FIRST_GLYPH && code <= LAST_GLYPH ? code : 63) - FIRST_GLYPH;
        this.glyphColors[n] = color;
        n++;
      }
    }
    return n;
  }
}

function program(gl: WebGL2RenderingContext, vertexSource: string, fragmentSource: string): WebGLProgram {
  const result = gl.createProgram();
  for (const [type, source] of [
    [gl.VERTEX_SHADER, vertexSource],
    [gl.FRAGMENT_SHADER, fragmentSource]
  ] as const) {
    const shader = gl.createShader(type);
    if (!shader) {
      throw new RendererUnavailableError('webgl2', 'Could not create a shader.');
    }
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      throw new RendererUnavailableError('webgl2', `Shader: ${gl.getShaderInfoLog(shader) ?? 'failed to compile'}`);
    }
    gl.attachShader(result, shader);
  }
  gl.linkProgram(result);
  if (!gl.getProgramParameter(result, gl.LINK_STATUS)) {
    throw new RendererUnavailableError('webgl2', `Program: ${gl.getProgramInfoLog(result) ?? 'failed to link'}`);
  }
  return result;
}

function buffer(gl: WebGL2RenderingContext): WebGLBuffer {
  const result = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, result);
  return result;
}

function vertexArray(gl: WebGL2RenderingContext): WebGLVertexArrayObject {
  const result = gl.createVertexArray();
  gl.bindVertexArray(result);
  return result;
}

function bindCorners(gl: WebGL2RenderingContext, corners: WebGLBuffer): void {
  gl.bindBuffer(gl.ARRAY_BUFFER, corners);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
}

/** Points per-instance attribute `location` at the bound buffer. */
function instanced(gl: WebGL2RenderingContext, location: number, size: number, type: number, normalized: boolean) {
  gl.enableVertexAttribArray(location);
  gl.vertexAttribPointer(location, size, type, normalized, 0, 0);
  gl.vertexAttribDivisor(location, 1);
}
