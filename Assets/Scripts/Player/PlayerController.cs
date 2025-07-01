using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using Voxels.CSG;
using Voxels.Materials;
using World;

namespace Player
{
    [RequireComponent(typeof(CharacterController), typeof(PlayerInput))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")] [Min(1.0f)] [SerializeField]
        private float m_movementSpeed = 5.0f;

        [Min(1.0f)] [SerializeField] private float m_sprintMultiplier = 3.0f;

        [Header("Look")] [Range(0.0f, 1.0f)] [SerializeField]
        private float m_lookSensitivity = 0.05f;

        [SerializeField] private Camera m_camera;

        [Header("Interaction")] [SerializeField]
        private CSGPrimitiveType m_interactionPrimitiveType = CSGPrimitiveType.Cuboid;

        private CharacterController m_controller;
        private int m_playerLayerMask;

        private float2 m_moveDelta;
        private float2 m_lookDelta;
        private float2 m_rotation;
        private float3 m_velocity;
        private bool m_primaryDown;
        private bool m_secondaryDown;
        private float m_verticalMoveDelta;
        private bool m_sprintDown;

        private void Start()
        {
            m_controller = GetComponent<CharacterController>();
            m_playerLayerMask = LayerMask.GetMask("Player");
            Cursor.lockState = CursorLockMode.Locked;
        }

        private void Update()
        {
            ApplyMovement();
            ApplyLook();
            HandleWorldInteraction();
        }

        public void OnMove(InputValue value) => m_moveDelta = value.Get<Vector2>();
        
        public void OnVerticalMove(InputValue value) => m_verticalMoveDelta = value.Get<float>();

        public void OnLook(InputValue value) => m_lookDelta = value.Get<Vector2>();

        public void OnPrimary(InputValue value) => m_primaryDown = value.isPressed;

        public void OnSecondary(InputValue value) => m_secondaryDown = value.isPressed;
        
        public void OnSprint(InputValue value) => m_sprintDown = value.isPressed;

        private void ApplyMovement()
        {
            // Calculate the current movement speed (with sprint multiplier if sprinting)
            float currentSpeed = m_sprintDown ? m_movementSpeed * m_sprintMultiplier : m_movementSpeed;
            
            // Create the movement vector in camera-relative space
            Vector3 movement = Vector3.zero;
            
            // Forward/backward - use camera's forward vector (including vertical component)
            if (m_moveDelta.y != 0)
            {
                movement += m_camera.transform.forward * m_moveDelta.y;
            }
            
            // Left/right - use camera's right vector (keeping it horizontal)
            if (m_moveDelta.x != 0)
            {
                // For side-to-side movement, we want to stay on the horizontal plane
                Vector3 rightDir = m_camera.transform.right;
                movement += rightDir * m_moveDelta.x;
            }
            
            // Up/down movement (typically Q/E keys) - world space up/down
            if (m_verticalMoveDelta != 0)
            {
                movement += Vector3.up * m_verticalMoveDelta;
            }
            
            // Apply the movement
            m_controller.Move(movement * currentSpeed * Time.deltaTime);
        }

        private void ApplyLook()
        {
            m_rotation.y += m_lookDelta.x * m_lookSensitivity;
            m_rotation.x -= m_lookDelta.y * m_lookSensitivity;
            m_rotation.x = math.clamp(m_rotation.x, -90.0f, 90.0f);

            m_camera.transform.localRotation = Quaternion.Euler(m_rotation.x, 0.0f, 0.0f);
            transform.localRotation = Quaternion.Euler(0.0f, m_rotation.y, 0.0f);

            m_lookDelta = 0.0f;
        }

        private void HandleWorldInteraction()
        {
            Ray ray = new Ray(m_camera.transform.position, m_camera.transform.forward);

            if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, ~m_playerLayerMask))
            {
                GPUCSGPrimitive primitive = new GPUCSGPrimitive(m_interactionPrimitiveType);
                float3 scale = 4.0f;

                WorldManager.Instance.DrawCSGPrimitiveHologram(primitive.PrimitiveType, hit.point, scale);

                if (m_primaryDown)
                {
                    WorldManager.Instance.ApplyCSGOperation(new GPUCSGOperator(CSGOperatorIndex.Union), primitive,
                        MaterialIndex.Dirt, hit.point, scale);
                }

                if (m_secondaryDown)
                {
                    WorldManager.Instance.ApplyCSGOperation(new GPUCSGOperator(CSGOperatorIndex.Difference), primitive,
                        default, hit.point, scale);
                }
            }
        }
    }
}