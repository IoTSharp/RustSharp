fn hidden_owner() {
    let hidden: i32 = 1;
}

pub struct Record {
    pub field: i32,
}

enum Choice<T> {
    One(T),
}

type Invalid = Choice::T;

fn inspect() {
    crate::hidden_owner::hidden;
    crate::Record::field;
}
