//! Arithmetic exported through the module's glob import.

mod values;

/// Produces the sample's answer.
pub(crate) fn answer() -> i32 {
    //! The nested module supplies the base value.
    values::base() + 2
}

pub(crate) fn positive() -> bool {
    answer() > 0
}
