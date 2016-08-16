﻿using System.Collections;
using UnityEngine;
using UnityEngine.VR.Utilities;

public class BlinkVisuals : MonoBehaviour
{
	private enum State
	{
		Inactive = 0,
		TransitioningIn = 1,
		TransitioningOut = 2
	}

	[SerializeField]
	private Color m_InvalidLocationColor;
	[SerializeField]
	private LayerMask m_LayerMask = -1;
	[SerializeField]
	private int m_LineSegmentCount = 10;
	[SerializeField]
	private float m_MaxArc = 0.85f;
	[SerializeField]
	private float m_Radius = 0.5f;
	[SerializeField]
	private float m_Range = 5f;
	[SerializeField]
	private Color m_ValidLocationColor;

	[SerializeField]
	private VRLineRenderer m_LineRenderer;
	[SerializeField]
	private Transform m_LocatorRoot;
	[SerializeField]
	private GameObject m_MotionIndicatorSphere;
	[SerializeField]
	private int m_MotionSphereCount = 10;
	[SerializeField]
	private MeshRenderer m_RingRenderer;
	[SerializeField]
	[Tooltip("Room-Scale mesh that will be instantiated to visually represent the surrounding room-scale bounds.")]
	private MeshRenderer m_RoomScaleRenderer;
	[SerializeField]
	private MeshRenderer m_TubeRenderer;

	private readonly Vector3 kGroundOffset = Vector3.one * 0.01f; // Used to offset the room scale visuals to avoid z-fighting
	private readonly string kTintColor = "_TintColor";

	private float m_CurveLengthEstimate;
	private Vector3? m_DetachedWorldArcPosition;
	private Vector3 m_FinalPosition;
	private Vector3 m_LastPosition;
	private Quaternion m_LastRotation;
	private MeshRenderer m_LineRendererMeshRenderer;
	private float m_MotionSphereOffset;
	private Transform[] m_MotionSpheres;
	private Vector3 m_MotionSphereOriginalScale;
	private float m_MovementMagnitudeDelta;
	private Vector3 m_MovementVelocityDelta;
	private bool m_OutOfMaxRange;
	private readonly Vector3[] m_BezierControlPoints = new Vector3[4]; // Cubic
	private Transform m_RingTransform;
	private Vector3 m_RingTransformOriginalScale;
	private Vector3 m_RoomScaleLazyPosition;
	private Transform m_RoomScaleTransform;
	private Vector3[] m_SegmentPositions;
	private State m_State = State.Inactive;
	private Transform m_ToolPoint;
	private Transform m_Transform;
	private Transform m_TubeTransform;
	private Vector3 m_TubeTransformHiddenScale;
	private Vector3 m_TubeTransformOriginalScale;
	private bool m_ValidTarget;
	private bool m_EndVisualDisplay;

	public Vector3 locatorPosition { get { return locatorRoot.position; } }
	public Transform locatorRoot { get { return m_LocatorRoot; } }
	public bool validTarget { get { return m_ValidTarget; } }

	private float pointerStrength { get { return (m_ToolPoint.forward.y + 1.0f) * 0.5f; } }

	void Awake()
	{
		m_LineRenderer = GetComponent<VRLineRenderer>();
		m_LineRendererMeshRenderer = m_LineRenderer.GetComponent<MeshRenderer>();
	}

	public void Start()
	{
		m_Transform = transform;

		if (m_ToolPoint == false) m_ToolPoint = transform;

		m_LineRenderer.SetVertexCount(m_LineSegmentCount);
		m_LineRenderer.useWorldSpace = true;

		m_MotionSpheres = new Transform[m_MotionSphereCount];
		for (int i = 0; i < m_MotionSphereCount; i++)
		{
			m_MotionSpheres[i] = ((GameObject)Instantiate(m_MotionIndicatorSphere, m_ToolPoint.position, m_ToolPoint.rotation)).transform;
			m_MotionSpheres[i].SetParent(m_Transform);
			m_MotionSpheres[i].name = "motion-sphere-" + i;
			m_MotionSpheres[i].gameObject.SetActive(false);
		}
		m_MotionSphereOriginalScale = m_MotionSpheres[0].localScale;
		m_CurveLengthEstimate = 1.0f;
		m_MotionSphereOffset = 0.0f;

		m_RoomScaleTransform = m_RoomScaleRenderer.transform;
		m_RoomScaleTransform.parent = m_LocatorRoot;
		m_RoomScaleTransform.localPosition = Vector3.zero;
		m_RoomScaleTransform.localRotation = Quaternion.identity;

		m_RingTransform = m_RingRenderer.transform;
		m_RingTransformOriginalScale = m_RingTransform.localScale;
		m_TubeTransform = m_TubeRenderer.transform;
		m_TubeTransformOriginalScale = m_TubeTransform.localScale;
		m_TubeTransformHiddenScale = new Vector3(m_TubeTransform.localScale.x, 0.0001f, m_TubeTransform.localScale.z);

		ShowLine(false);
	}

	void Update()
	{
		if (m_State != State.Inactive)
		{
			const float kMotionSphereSpeed = 0.125f;
			m_MotionSphereOffset = (m_MotionSphereOffset + (Time.unscaledDeltaTime * kMotionSphereSpeed)) % (1.0f / (float)m_MotionSphereCount);

			if (m_LastPosition != m_Transform.position || m_LastRotation != m_Transform.rotation)
			{
				DrawArc();
				m_LastPosition = m_Transform.position;
				m_LastRotation = m_Transform.rotation;
			}
			DrawMotionSpheres();

			const float kSmoothTime = 0.0875f;
			const float kMaxSpeed = 100f;
			m_RoomScaleTransform.position = Vector3.SmoothDamp(m_RoomScaleLazyPosition, m_LocatorRoot.position, ref m_MovementVelocityDelta, kSmoothTime, kMaxSpeed, Time.unscaledDeltaTime);
			// Since the room scale visuals are parented under the locator root it is necessary to cache the position each frame before the locator root gets updated
			m_RoomScaleLazyPosition = m_RoomScaleTransform.position;
			m_MovementMagnitudeDelta = (m_RoomScaleTransform.position - m_LocatorRoot.position).magnitude;

			const float kTubeHiddenDistanceThreshold = 6f;
			m_TubeTransform.localScale = Vector3.Lerp(m_TubeTransformOriginalScale, m_TubeTransformHiddenScale, m_MovementMagnitudeDelta / kTubeHiddenDistanceThreshold);
		}
		else if (!m_EndVisualDisplay && m_OutOfMaxRange && Mathf.Abs(pointerStrength) < m_MaxArc)
		{
			m_OutOfMaxRange = false;
			ShowVisuals();
		}
	}

	public void ShowVisuals()
	{
		if (m_State == State.Inactive || m_State == State.TransitioningOut)
		{
			m_RoomScaleLazyPosition = m_RoomScaleTransform.position;

			for (int i = 0; i < m_MotionSphereCount; ++i)
				m_MotionSpheres[i].gameObject.SetActive(true);
			
			m_EndVisualDisplay = false;
			StartCoroutine(AnimateShowVisuals());
		}
	}

	public void HideVisuals()
	{
		if (m_State != State.Inactive)
		{
			StopAllCoroutines();
			m_EndVisualDisplay = true;
			StartCoroutine(AnimateHideVisuals());
		}

		m_OutOfMaxRange = false;
	}

	private IEnumerator AnimateShowVisuals()
	{
		m_State = State.TransitioningIn;
		m_RoomScaleTransform.position = m_FinalPosition + kGroundOffset;
		ShowLine();

		for (int i = 0; i < m_MotionSphereCount; ++i)
		{
			m_MotionSpheres[i].localScale = m_MotionSphereOriginalScale;
		}

		const float kTargetScale = 1f;
		const float kTargetSnapThreshold = 0.05f;
		const float kEaseStepping = 8f;

		float scale = 0f;
		float tubeScale = m_TubeTransform.localScale.x;
		while (m_State == State.TransitioningIn && scale < 1)
		{
			m_TubeTransform.localScale = new Vector3(tubeScale, scale, tubeScale);
			m_LocatorRoot.localScale = Vector3.one * scale;
			m_LineRenderer.SetWidth(scale, scale);
			scale = U.Math.Ease(scale, kTargetScale, kEaseStepping, kTargetSnapThreshold);
			yield return null;
		}

		if (m_State == State.TransitioningIn)
			m_LineRenderer.SetWidth(kTargetScale, kTargetScale);
	}

	private IEnumerator AnimateHideVisuals()
	{
		m_State = State.TransitioningOut;
		m_DetachedWorldArcPosition = m_LocatorRoot.position;

		const float kTargetScale = 0f;
		const float kTargetSnapThreshold = 0.0005f;
		const float kEaseStepping = 8f;

		float scale = 1f;
		float tubeScale = m_TubeTransform.localScale.x;
		while (m_State == State.TransitioningOut && scale > 0.0001f)
		{
			SetColors(Color.Lerp(validTarget == true ? m_ValidLocationColor : m_InvalidLocationColor, Color.clear, 1f - scale));
			m_TubeTransform.localScale = new Vector3(tubeScale, scale, tubeScale);
			m_LineRenderer.SetWidth(scale, scale);
			scale = U.Math.Ease(scale, kTargetScale, kEaseStepping, kTargetSnapThreshold);
			m_RingTransform.localScale = Vector3.Lerp(m_RingTransform.localScale, m_RingTransformOriginalScale, scale);
			yield return null;
		}

		m_DetachedWorldArcPosition = null;

		// set value if no additional transition has begun
		if (m_State == State.TransitioningOut)
		{
			m_RingTransform.localScale = m_RingTransformOriginalScale;
			m_State = State.Inactive;
			m_LineRenderer.SetWidth(kTargetScale, kTargetScale);
			ShowLine(false);

			for (int i = 0; i < m_MotionSphereCount; ++i)
				m_MotionSpheres[i].gameObject.SetActive(false);
		}
	}

	public void DrawArc()
	{
		m_LocatorRoot.rotation = Quaternion.identity;

		// prevent rendering line when pointing to high or low
		if (Mathf.Abs(pointerStrength) > m_MaxArc)
		{
			m_ValidTarget = false;
			m_OutOfMaxRange = true;
			StartCoroutine(AnimateHideVisuals());
			return;
		}

		// start point
		m_BezierControlPoints[0] = m_ToolPoint.position;
		// first handle -- determines how steep the first part will be
		m_BezierControlPoints[1] = m_ToolPoint.position + m_ToolPoint.forward * pointerStrength * m_Range;

		const float kArcEndHeight = -2f;
		m_FinalPosition = new Vector3(m_BezierControlPoints[1].x, kArcEndHeight, m_BezierControlPoints[1].z);
		// end point
		m_BezierControlPoints[3] = m_FinalPosition;
		// second handle -- determines how steep the intersection with the ground will be
		m_BezierControlPoints[2] = m_FinalPosition;

		// set the position of the locator
		m_LocatorRoot.position = m_DetachedWorldArcPosition == null ? m_FinalPosition + kGroundOffset : (Vector3)m_DetachedWorldArcPosition;

		m_ValidTarget = false;

		// TODO: Switch to support for the new EVR collider-free system
		var colliders = Physics.OverlapSphere(m_FinalPosition, m_Radius, m_LayerMask.value);
		m_ValidTarget = colliders != null && colliders.Length > 0;

		SetColors(m_ValidTarget ? m_ValidLocationColor : m_InvalidLocationColor);

		// calculate and send points to the line renderer
		m_SegmentPositions = new Vector3[m_LineSegmentCount];

		for (int i = 0; i < m_LineSegmentCount; i++)
		{
			var t = i / (float)Mathf.Max((m_LineSegmentCount - 1), 1);
			var q = U.Math.CalculateCubicBezierPoint(t, m_BezierControlPoints);
			m_SegmentPositions[i] = q;
		}
		m_LineRenderer.SetPositions(m_SegmentPositions);

		// The curve length will be somewhere between a straight line between the points 
		// and a path that directly follows the control points.  So we estimate this by just averaging the two.
		m_CurveLengthEstimate = ((m_BezierControlPoints[3] - m_BezierControlPoints[0]).magnitude + ((m_BezierControlPoints[1] - m_BezierControlPoints[0]).magnitude + (m_BezierControlPoints[1] - m_BezierControlPoints[2]).magnitude)) * 0.5f;
	}

	public void DrawMotionSpheres()
	{
		// We estimate how much we should correct our curve time by with a guess step
		for (int i = 0; i < m_MotionSphereCount; ++i)
		{
			var t = (i / (float)m_MotionSphereCount) + m_MotionSphereOffset;
			m_MotionSpheres[i].position = U.Math.CalculateCubicBezierPoint(t, m_BezierControlPoints);
			float validTargetEase = m_State == State.TransitioningIn ? (m_ValidTarget == true ? m_MotionSphereOriginalScale.x : 0.05f) : 0f;
			validTargetEase = U.Math.Ease(m_MotionSpheres[i].localScale.x, validTargetEase, 16, 0.0005f) * Mathf.Min((m_Transform.position - m_MotionSpheres[i].position).magnitude * 4, 1f);
			m_MotionSpheres[i].localScale = Vector3.one * validTargetEase;
			m_MotionSpheres[i].localRotation = Quaternion.identity;

			// If we're not at the starting point, we apply a correction factor
			if (t > 0.0f)
			{
				// We have how long we *think* the curve should be
				var lengthEstimate = (m_CurveLengthEstimate * t);

				// We compare that to how long our distance actually is
				var correctionFactor = lengthEstimate / (m_MotionSpheres[i].position - m_BezierControlPoints[0]).magnitude;

				// We then scale our time value by this correction factor
				var correctedTime = Mathf.Clamp01(t * correctionFactor);
				m_MotionSpheres[i].position = U.Math.CalculateCubicBezierPoint(correctedTime, m_BezierControlPoints);
			}
		}
	}

	public void ShowLine(bool show = true)
	{
		m_LocatorRoot.gameObject.SetActive(show);
		m_LineRendererMeshRenderer.enabled = show;

		if (!show)
		{
			m_RoomScaleRenderer.sharedMaterial.SetColor(kTintColor, Color.clear);

			for (int i = 0; i < m_MotionSphereCount; ++i)
				m_MotionSpheres[i].localScale = Vector3.zero;
		}
	}

	void SetColors(Color color)
	{
		m_LineRenderer.SetColors(color, color);
		m_MotionSpheres[0].GetComponent<MeshRenderer>().sharedMaterial.color = color;
		// Set the color for all object sharind the blink material
		m_RoomScaleRenderer.sharedMaterial.SetColor(kTintColor, color);
	}
}