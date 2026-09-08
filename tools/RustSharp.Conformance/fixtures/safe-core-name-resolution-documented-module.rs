//! Module documentation is inert.
/// Public API documentation.
mod api {
    //! Inner module documentation.
    /// Callable documentation.
    pub fn f() {}
}
use crate::api::{self as facade, f as run};
fn main() { run(); facade::f(); }
